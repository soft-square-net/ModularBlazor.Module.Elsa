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
| `content.assign`, `content.transfer`, `content.publish` | the coordinator; **Assign in shift**, **Work in shift**, **Desk review** (handover of a review) and **Publish post** (also `publish-due`) |
| `shifts.manage` | the coordinator (`GET /api/shifts/overdue` and the ended list) |
| `content.unit.assign` | filing a post in its Section and a content in its unit; it is also what lets the service see posts |
| `newsroom.workflow` | reading projects and reporting their states; bindings, routing and workload |
| `org.read` | Organization's members of a unit, to find its managers when a deadline passes and no lead is on shift |
| `dynamicfields.read` | Content's publish has DynamicFields check the post as the caller; without it publishing answers `503` |

A missing grant does not fault a newsroom workflow: the activity logs a warning, tries again, and goes on once the grant is there.

## Newsroom workflows

NewsRoom owns post projects; Elsa runs the workflow of each.

### Events

The module subscribes to these integration events through the platform's inbox (tables `elsa_events.integration_inbox` and `integration_outbox` in each tenant's database; the migration is PostgreSQL only):

| Event | What Elsa does |
|---|---|
| `newsroom.project-approved.v1`, `newsroom.project-binding-available.v1` | starts the bound workflow, one instance per tenant and project |
| `newsroom.project-reapproved.v1` | starts it if the project has none, else wakes the waits |
| `newsroom.project-state-changed.v1` with `Killed` | cancels the project's workflows, sub-workflows included |
| other project state changes, `content.work-item-*`, `content.post-*`, `shifts.checked-in.v1`, `shifts.checked-out.v1`, `shifts.handover.v1`, `shifts.ended.v1`, `shifts.published.v1`, `shifts.member-added.v1` | tells the activities that wait on that project, content, post or unit to look again |

The event records are copied from `NewsRoom.Contracts`; the module references no NewsRoom package.

- **Which workflow.** NewsRoom's bindings say, per post kind and unit (`GET /api/newsroom/projects/{id}/workflow`), with
  an optional definition version. Without a binding no workflow starts, and no default is assumed: NewsRoom reports a
  setup error on the project and publishes `project-binding-available` once there is one. To use a
  [default workflow](#default-workflows), bind the post kind to its id.
- **The instance** gets the input `projectId`, `postId`, `unitId` (the Section) and `postKind`, and the correlation id
  `newsroom-project:{projectId}`. Activities use these when their own `ProjectId`, `PostId`, `ContentId` or `UnitId`
  input is not set.
- **Events may repeat, come late or out of order.** A wait never trusts what an event said: it reads the owning module's
  current state, and a signal or a timer only makes it look again. A lost event delays a workflow by at most
  `Elsa:Newsroom:RecheckInterval`.
- **Replicas.** A deterministic instance id and a PostgreSQL distributed lock keep two replicas from starting a project
  or a sub-workflow twice, or running an instance at once. Each replica sweeps, per tenant, the timers that are overdue
  because the replica that held them stopped.

### Default workflows

Two workflows ship in code. Every tenant has them under the same definition id.

| Definition id | What it does |
|---|---|
| `newsroom-post` | files the post in its Section (a post the Section cannot take waits for the moderator) → produces its contents → desk review (a returned post is produced again) → publishes (a refused publication tells the proposal's author and goes back to the desk) → tells the author |
| `newsroom-content` | gives the content to the unit its kind is routed to → has the people on shift there produce it until it is accepted |

- **Using them.** A post kind runs `newsroom-post` only where NewsRoom binds it. A content runs the definition bound to
  its content kind (its field key) in the unit that produces it, else `newsroom-content`. That unit is the one NewsRoom's
  routing rules give the kind, else the Section.
- **Overriding.** Author another definition in Elsa Studio and bind it in NewsRoom. Definition ids are unique across
  tenants (see [Engine hosting](#engine-hosting)), so give each tenant's copy its own id.
- **Sub-workflows.** **Produce contents** starts one per content of the post, also for contents added later, with the
  main instance as parent and the same correlation id. It does not wait for them to end: it waits for the post. It is
  satisfied when the post is submitted, or while it is a draft, when it has required contents and all are accepted.

**A released version of a code workflow never changes.** Elsa 3.9.0 keeps no copy of it: a suspended instance resumes on
whatever the code builds now. Tried on PostgreSQL:

| Change in code | What a suspended instance did |
|---|---|
| same version, an input edited | ran the new input |
| same version, a step inserted in front | resumed the wrong activity, without an error |
| new version, the old class removed | could not be resumed: every signal threw |
| new version, the old class kept | finished on the old graph; new instances ran the new one |

So a change is a new class with the next version, registered beside the earlier ones (`DefaultWorkflows.Register`), which
stay as long as an instance may use them. A test holds each version to its recorded graph; it does not cover input
values or what an activity does. Changing an activity's behaviour changes every instance that runs it, so keep
activities compatible with the instances that wait in them. A binding may pin a version.

### Activities

Their type names in a definition are `SoftSquare.Newsroom.<name>`.

| Activity | Inputs (besides `ProjectId` / `PostId`) | Outcomes | Outputs |
|---|---|---|---|
| `RouteToUnit` | `ContentKind`, `Categories` | `Routed`, `NoRoute` | `UnitId` |
| `EnsurePostInUnitCategories` | `UnitId` (default: the Section) | `Filed`, `NeedsModeration` | `Reason` |
| `WaitForRefiling` | | `Refiled`, `ProjectEnded` | |
| `ProduceContents` | `ContentDefinitionId` (default: bound per content kind) | `Produced`, `Refiled`, `ProjectEnded` | |
| `FileContentInUnit` | `ContentId`, `ContentKind`, `UnitId` (default: routed) | `Filed`, `Kept`, `ProjectEnded` | `FiledIn` |
| `AssignInShift` | `ContentId`, `RoleKey`, `UnitId`, `ManagerDelay` | `Assigned`, `ProjectEnded` | `AssigneeId` |
| `WorkInShift` | `ContentId`, `RoleKey`, `UnitId`, `ManagerDelay`, `Stages` | `Reached`, `ProjectEnded` | `AssigneeId` |
| `WaitForStage` | `ContentId`, `Stages` (default `accepted`) | `Reached`, `ProjectEnded` | `Stage` |
| `Deadline` | `ContentId`, `Stages`, `Due` or `DueIn` | `Met`, `Escalated`, `ProjectEnded` | `EscalatedTo` |
| `DeskReview` | `ReviewerRoleKey`, `ReviewerDelay`, `EndOnRefiling` | `Approved`, `Returned`, `Refiled` | |
| `PublishPost` | `PublishAt` | `Published`, `Refused` | `Reason` |
| `Notify` | `UserId`, `ToProjectAuthor` or `UnitPath`, `NotificationType`, `Message` | `Done` | |

- **Refiling.** `EnsurePostInUnitCategories` does not stay waiting. To check again when the post's categories or unit
  change, or a moderator approves the project again, connect it to `WaitForRefiling`, and that back to it. On
  `NeedsModeration` it first clears the post's unit, so the moderator can change the categories; the workflow does not
  fault. `ProduceContents` and, with `EndOnRefiling`, `DeskReview` end with `Refiled` on such a change.
- **Assignment** picks, among the role's holders checked in to a running shift of the unit, the one with the least open
  work by NewsRoom's count.
- **A deadline** that passes sends `workflow.deadline-missed` to the running shift's leads, else to the managers of the
  unit or of the nearest unit above it, else to the unit.
- **A project that is published**, in the workflow or by hand (NewsRoom follows Content), ends the waits above with
  `ProjectEnded`. In a `Sequence` the workflow then goes on; in a flowchart, leave the outcome unconnected to end there.
- **Killing** a project cancels its instances. Until the kill's event arrives, their waits do nothing. It does not cancel
  a publication already scheduled in Content, which has no call for that.

### Handover in a workflow

- **Content.** `WorkInShift` remembers the shift in which the assignee holds the work. When that shift has ended, it
  assigns the content to the least loaded holder of the role on a running shift, unless the assignee goes on in one.
  Someone it never saw on a shift of the unit was assigned by hand and keeps the work.
- **Reviews.** When the shift of a post in review has ended, `DeskReview` returns the review to the queue (Content's
  transfer).
- **New reviewers.** Only with `ReviewerRoleKey` (`Elsa:Newsroom:ReviewerRole` in the default workflow) does the
  workflow accept a submission and assign a reviewer, never the post's owner, after `ReviewerDelay`. Content allows the
  service to do that only with `Content:Newsroom:WorkflowOwnsAssignment`; without it, leave the setting empty and people
  or the coordinator assign.

### Scheduled publishing

`PublishPost` with `PublishAt` schedules the post. Content publishes a due post only when asked, so at that time the
activity calls `publish-due`, which publishes every due post of the tenant.

### Dry run

`POST /api/elsa/newsroom/dry-run` (`workflow.design`) says what a workflow would do with a post as things are now. It
makes no instance and changes nothing in any module.

```json
{ "postId": 12, "unitId": "…", "definitionId": "…", "definitionVersion": 1 }
```

Only `postId` is required: without `unitId` the post's unit is used, and without `definitionId` the binding of the
post's kind there. The answer has the definition, its steps in order (`activityId`, `activity`, `description`,
`outcome`, `wouldWait`, and for a content, the steps of its sub-workflow) and `problems`.

- It follows the way taken when every wait is met in the end. It stops where the present state decides otherwise, such
  as a Section that cannot take the post (`outcome` is null there).
- It reads literal inputs and module settings. It evaluates no expressions, so a decision or a custom activity of a
  definition authored in Elsa Studio is reported as not tried and taken to end with `Done`.
- It reads the modules as `elsa-workflows`, which is why viewing is not enough to run it.

### Settings

| Module setting | Default |
|---|---|
| `Elsa:Newsroom:ContentRole` (the shift role that produces contents, in `newsroom-content`) | `writer` |
| `Elsa:Newsroom:ManagerDelay` (to assign a content by hand first) | `00:05:00` |
| `Elsa:Newsroom:ReviewerRole` (see [Handover](#handover-in-a-workflow)) | empty |
| `Elsa:Newsroom:ReviewerDelay` | `00:05:00` |
| `Elsa:Newsroom:RecheckInterval` | `00:05:00` |
| `Elsa:Newsroom:SettleDelay` (the extra look right after a wait begins) | `00:00:02` |
| `Elsa:Newsroom:RetryDelay` (after a conflict, or a module that did not answer) | `00:00:30` |
| `Elsa:Newsroom:TimerSweepInterval` | `00:01:00` |
| `Elsa:Newsroom:TimerSweepGrace` (how overdue a timer is before the sweep takes it) | `00:00:30` |

`Elsa:Newsroom:ProjectWorkflow` no longer exists: bind post kinds in NewsRoom instead. Workflows that an earlier version
left suspended go on after the upgrade. A `Rules` input of `RouteToUnit` in a saved definition is ignored: the rules are
NewsRoom's now.

## Shift handover

For every shift that ended (checked out by a person or by `ShiftClock`), Elsa calls Content's handover, which returns the shift's unfinished review work to the queue.

- **Where it reads.** `GET /api/shifts/shifts/ended?since={utc}&take=500` (`shifts.manage`), oldest end first.
- **Cursor.** One per tenant, **in memory**: the newest `endedAt` seen. Each read goes back five minutes behind it, because a shift can be saved after one with a later end was read. Handled shifts are skipped by id.
- **Failures.** Only a successful handover counts. After a failed one (any other status, `404` and `409` included), the cursor stays at that shift and the next poll retries it; the other shifts are still handed over.
- **Restarts.** A new process looks back `Elsa:HandoverLookback` (default `1.00:00:00`, one day) and hands those shifts over again; the handover is idempotent. A shift that ended longer ago than that while Elsa was down, or whose handover was still failing at a restart, may not be retried. Write the value as `days.hours:minutes:seconds`: `24:00:00` means 24 days.
- **Replicas** each keep their own cursor and may repeat a handover.
- **Older Shifts.** When the ended list answers `404`, Elsa uses `GET /api/shifts/overdue` and calls the handover once the shift is past its end plus the checkout grace. That list holds only shifts not checked out yet, so a shift checked out before Elsa's next poll past the grace gets no handover there.

The coordinator reconciles persisted business deadlines on a configurable polling interval, and still runs next to the
newsroom workflows. Business state transitions stay in the owning modules. The default workflows do **not** replace it
yet. Without it, nothing would:

- publish a scheduled post that no running `PublishPost` waits for (scheduled by hand, or without a project);
- hand over the reviews of posts that are not in a running `DeskReview` (without a project, or whose workflow ended);
- assign reviewers of accepted posts, unless `Elsa:Newsroom:ReviewerRole` is set, and then only for posts in a running
  `DeskReview`;
- call Content's whole-shift handover: `DeskReview` transfers one post's review at a time.

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
