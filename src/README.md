# Optional Elsa workflow module

`WebApi.Modules.Elsa.Infrastructure` is a custom runtime package. It is not referenced by the WebApi host and can be omitted from deployments that do not use newsroom automation.

## Deployment modes

- **Single process/pool:** activate `elsa`, `content`, and `shifts` in the same API pool. The Elsa hosted coordinator calls the Content and Shifts APIs through the gateway.
- **Separated workflow pool:** run dedicated WebApi processes with `Modules:Pool=workflow`, `Modules:Role=Api`, and `Modules:Enabled=elsa`. Route `/api/elsa/**` and the Content/Shifts APIs to their owning pools. `HotUnload=false`; upgrade by rolling restart.
- **ERP configuration:** omit Content, Shifts, and Elsa. Identity, tenancy, and unrelated modules have no project dependency on them.

The modules communicate through HTTP and the host's tenant context, not project references. Content owns creative works and validates state changes; Shifts owns rota and shift timing; Elsa coordinates delayed assignment, scheduled publishing, and the handover of unfinished reviews. Shifts checks ended shifts out itself (its `ShiftClock` service); Elsa never checks a shift out. Persisted timestamps and state in Content/Shifts are authoritative, so a restarted coordinator can reconcile overdue actions. Domain endpoints recheck permissions and current state on every command.

## Setup

1. Build this project and package the **contents** of `bin/Debug/net10.0` as a zip for the runtime module installer.
2. Install `elsa` in the shared module catalog and activate it only in the workflow pool. Install Content and Shifts the same way, activating them in the pools that own their APIs.
3. Configure the `ConnectionStrings:elsa` secret for the workflow host. The host passes it only to the Elsa module; it is not stored in `modules.json`.
4. Configure the same confidential OAuth client secret for Identity and the workflow pool. In the Aspire Hybird AppHost, supply the `elsa-client-secret` parameter from Aspire user-secrets or the deployment secret provider.
5. Grant service subject `elsa-workflows` the [grants](#grants) below, in each tenant.
6. Ensure replicas share the module catalog/package storage and configure the Elsa database with durable storage/backups. PostgreSQL is the provider wired by this initial module.

Section settings control the assignment delay (default five minutes) and checkout grace (default fifteen minutes), after which Shifts checks the shift out. Assignment delay begins when the manager accepts the submission. Editing a live creative work retains its published snapshot and cancels a pending schedule pending Desk reapproval.

## Grants

Tenant-wide, for the service subject `elsa-workflows`. They are tenant-scoped and must be provisioned for each tenant, new ones included (`POST /api/identity/grants` by someone with `access.grants.manage`); do not add them to the client token as global permissions.

| Permission | Used for |
|---|---|
| `content.assign`, `content.transfer`, `content.publish` | the coordinator; **Assign in shift** and **Publish post** |
| `shifts.manage` | the coordinator (`GET /api/shifts/overdue` and the ended list) |
| `content.unit.assign` | filing a post in its Section; it is also what lets the service see posts |
| `newsroom.workflow` | reading projects and reporting their states |
| `org.read` | Organization's members of a unit, to find its managers when a deadline passes and no lead is on shift |
| `dynamicfields.read` | Content's publish has DynamicFields check the post as the caller; without it publishing answers `503` |

A missing grant does not fault a newsroom workflow: the activity logs a warning, tries again, and goes on once the grant is there.

## Newsroom workflows

NewsRoom owns post projects; Elsa runs the workflow of each.

### Events

The module subscribes to these integration events through the platform's inbox (tables `elsa_events.integration_inbox` and `integration_outbox` in each tenant's database; the migration is PostgreSQL only):

| Event | What Elsa does |
|---|---|
| `newsroom.project-approved.v1` | starts the bound workflow, one instance per tenant and project |
| `newsroom.project-state-changed.v1` with `Killed` | cancels the project's instance |
| `newsroom.project-reapproved.v1`, other project state changes, `content.work-item-*`, `content.post-*`, `shifts.checked-in.v1`, `shifts.published.v1`, `shifts.member-added.v1` | tells the activities that wait on that project, content, post or unit to look again |

The event records are copied from `NewsRoom.Contracts`; the module references no NewsRoom package.

- **Which workflow.** The module setting `Elsa:Newsroom:ProjectWorkflow` names one definition id for every project (`IWorkflowBindings`; bindings in NewsRoom come later). With none set, the delivery fails with an error that names the setting, and the platform treats it like any other failed event. A definition id is unique across tenants (see [Engine hosting](#engine-hosting)).
- **The instance** gets the input `projectId`, `postId`, `unitId` (the Section) and `postKind`, and the correlation id `newsroom-project:{projectId}`. Activities use these when their own `ProjectId`, `PostId` or `UnitId` input is not set.
- **Events may repeat, come late or out of order.** A wait never trusts what an event said: it reads the owning module's current state, and a signal or a timer only makes it look again. A lost event delays a workflow by at most `Elsa:Newsroom:RecheckInterval`.
- **Replicas.** A deterministic instance id and a PostgreSQL distributed lock keep two replicas from starting a project twice or running an instance at once. Each replica sweeps, per tenant, the timers that are overdue because the replica that held them stopped.

### Activities

Their type names in a definition are `SoftSquare.Newsroom.<name>`.

| Activity | Inputs (besides `ProjectId` / `PostId`) | Outcomes | Outputs |
|---|---|---|---|
| `RouteToUnit` | `ContentKind`, `Categories`, `Rules` (`contentKind`, `unitId` or `unitTag`) | `Routed`, `NoRoute` | `UnitId` |
| `EnsurePostInUnitCategories` | `UnitId` (default: the Section) | `Filed`, `NeedsModeration` | `Reason` |
| `WaitForRefiling` | | `Refiled` | |
| `AssignInShift` | `ContentId`, `RoleKey`, `UnitId`, `ManagerDelay` | `Assigned` | `AssigneeId` |
| `WaitForStage` | `ContentId`, `Stages` (default `accepted`) | `Reached` | `Stage` |
| `Deadline` | `ContentId`, `Stages`, `Due` or `DueIn` | `Met`, `Escalated` | `EscalatedTo` |
| `DeskReview` | | `Approved`, `Returned` | |
| `PublishPost` | `PublishAt` | `Published`, `Refused` | `Reason` |
| `Notify` | `UserId` or `UnitPath`, `NotificationType`, `Message` | `Done` | |

- **Refiling.** `EnsurePostInUnitCategories` does not stay waiting. To check again when the post's categories or unit change, or a moderator approves the project again, connect it to `WaitForRefiling`, and that back to it. On `NeedsModeration` it first clears the post's unit, so the moderator can change the categories; the workflow does not fault.
- **Assignment** picks, among the role's holders checked in to a running shift of the unit, the one with the fewest contents not accepted yet.
- **A deadline** that passes sends `workflow.deadline-missed` to the running shift's leads, else to the managers of the unit or of the nearest unit above it, else to the unit.
- **Killing** a project cancels its instance. It does not cancel a publication already scheduled in Content, which has no call for that.
- **A post published outside the workflow** ends `DeskReview` and `PublishPost`, but not a pending `AssignInShift`, `WaitForStage` or `Deadline`.

### Settings

| Module setting | Default |
|---|---|
| `Elsa:Newsroom:ProjectWorkflow` | none |
| `Elsa:Newsroom:RecheckInterval` | `00:05:00` |
| `Elsa:Newsroom:SettleDelay` (the extra look right after a wait begins) | `00:00:02` |
| `Elsa:Newsroom:RetryDelay` (after a conflict, or a module that did not answer) | `00:00:30` |
| `Elsa:Newsroom:TimerSweepInterval` | `00:01:00` |
| `Elsa:Newsroom:TimerSweepGrace` (how overdue a timer is before the sweep takes it) | `00:00:30` |

## Shift handover

For every shift that ended (checked out by a person or by `ShiftClock`), Elsa calls Content's handover, which returns the shift's unfinished review work to the queue.

- **Where it reads.** `GET /api/shifts/shifts/ended?since={utc}&take=500` (`shifts.manage`), oldest end first.
- **Cursor.** One per tenant, **in memory**: the newest `endedAt` seen. Each read goes back five minutes behind it, because a shift can be saved after one with a later end was read. Handled shifts are skipped by id.
- **Failures.** Only a successful handover counts. After a failed one (any other status, `404` and `409` included), the cursor stays at that shift and the next poll retries it; the other shifts are still handed over.
- **Restarts.** A new process looks back `Elsa:HandoverLookback` (default `1.00:00:00`, one day) and hands those shifts over again; the handover is idempotent. A shift that ended longer ago than that while Elsa was down, or whose handover was still failing at a restart, may not be retried. Write the value as `days.hours:minutes:seconds`: `24:00:00` means 24 days.
- **Replicas** each keep their own cursor and may repeat a handover.
- **Older Shifts.** When the ended list answers `404`, Elsa uses `GET /api/shifts/overdue` and calls the handover once the shift is past its end plus the checkout grace. That list holds only shifts not checked out yet, so a shift checked out before Elsa's next poll past the grace gets no handover there.

The current coordinator reconciles persisted business deadlines on a configurable polling interval. The Elsa engine, persistence, tenant adapters, and secured Elsa API are hosted by this module. It still runs next to the newsroom workflows; default definitions that replace it are a follow-up, and business state transitions stay in the owning modules.

## Engine hosting

- **Own container.** A runtime module's service container has no keyed services, and Elsa registers some. `ElsaEngine` therefore builds Elsa in its own container. The host lends it logging, the environment, data protection and the tenant directory. The engine starts Elsa's hosted services and runs Elsa's request pipeline. Register activities, workflows and their dependencies in `ElsaEngine.AddElsa`, not in `ElsaModule.ConfigureServices`.
- **Tenants.** A request runs in Elsa as the tenant the platform resolved, and Elsa filters definitions, instances and bookmarks by it. A request without a tenant is refused. **Code outside a request must go through `ElsaEngine.RunInTenantAsync`**, which refuses an empty, unknown or inactive tenant and runs the work with that tenant in a scope of the engine's container. Without that, Elsa raises no error: it stores and runs the instance under an empty tenant id, where no tenant sees it.
- **One database for all tenants** (`ConnectionStrings:elsa`, schema `elsa`), PostgreSQL only. Elsa's definition ids are unique across tenants: creating, through the API, a definition with an id another tenant uses answers `500` and changes nothing. Let Elsa generate ids, or make them unique. Workflows registered in code are different: every tenant runs them under the same id, with its own instances.
- **Timers.** `Elsa.Scheduling` provides `Delay`, `Timer` and the other scheduled activities. Elsa keeps timers in memory; the engine cancels them when it stops, and the timer sweep (see [Newsroom workflows](#newsroom-workflows)) resumes overdue delays that no replica holds.

## API permissions

The API is at `/api/elsa/elsa/api/...`, HTTP workflow triggers at `/api/elsa/workflows/...`. An anonymous request gets `401`. The caller needs a platform grant in the request's tenant; behind that check, Elsa's own permission checks all pass.

| Request | Permission |
|---|---|
| `/workflows/...`; `/elsa/api/` `workflow-instances`, `events`, `bookmarks`, `resume`, `tasks` (any method) | `workflow.run` |
| `execute`, `dispatch`, `bulk-dispatch` under `workflow-definitions` or `bulk-actions` (any method) | `workflow.run` |
| any other `GET` or `HEAD` | `workflow.view` |
| any other method | `workflow.design` |
