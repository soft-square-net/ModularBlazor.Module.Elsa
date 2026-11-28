# Optional Elsa workflow module

`WebApi.Modules.Elsa.Infrastructure` is a custom runtime package. It is not referenced by the WebApi host and can be omitted from deployments that do not use newsroom automation.

## Deployment modes

- **Single process/pool:** activate `elsa`, `content`, and `shifts` in the same API pool. The newsroom workflows call the other modules' APIs through the gateway.
- **Separated workflow pool:** run dedicated WebApi processes with `Modules:Pool=workflow`, `Modules:Role=Api`, and `Modules:Enabled=elsa`. Route `/api/elsa/**` and the Content/Shifts APIs to their owning pools. `HotUnload=false`; upgrade by rolling restart.
- **ERP configuration:** omit Content, Shifts, and Elsa. Identity, tenancy, and unrelated modules have no project dependency on them.

The modules communicate through HTTP and the host's tenant context, not project references. Content owns creative works and validates state changes; it publishes its own due posts, and returns to the queue a review whose reviewer can no longer review. Shifts owns rota and shift timing, and checks ended shifts out itself (its `ShiftClock` service); Elsa never checks a shift out. Elsa runs the [newsroom workflows](#newsroom-workflows), which assign work and reviews. State in the owning modules is authoritative, and their endpoints recheck permissions and current state on every command.

Elsa needs a Content that publishes its own due posts, hands over reviews and has `POST /posts/{id}/unschedule` (Content's E3 change). Upgrade the two together.

## Setup

1. Build this project and package the **contents** of `bin/Debug/net10.0` as a zip for the runtime module installer.
2. Install `elsa` in the shared module catalog and activate it only in the workflow pool. Install Content and Shifts the same way, activating them in the pools that own their APIs.
3. Configure the `ConnectionStrings:elsa` secret for the workflow host. The host passes it only to the Elsa module; it is not stored in `modules.json`.
4. Configure the same confidential OAuth client secret for Identity and the workflow pool. In the Aspire Hybird AppHost, supply the `elsa-client-secret` parameter from Aspire user-secrets or the deployment secret provider.
5. Grant service subject `elsa-workflows` the [grants](#grants) below, in each tenant.
6. Set up who assigns the work: see [Who assigns](#who-assigns).
7. Ensure replicas share the module catalog/package storage and configure the Elsa database with durable storage/backups. PostgreSQL is the provider wired by this initial module.

Editing a live creative work retains its published snapshot and cancels a pending schedule pending Desk reapproval.

## Who assigns

Turn these two on **together**, once the post kinds are bound in NewsRoom:

| Setting | Value |
|---|---|
| Content: `Content:Newsroom:WorkflowOwnsAssignment` | `true` |
| Elsa: `Elsa:Newsroom:ReviewerRole` | the key of the shift role that reviews, such as Shifts' own `editor`. The role must grant `content.review`: Content refuses a reviewer without it. |

Content then leaves the accept, assignment, transfer and return of a NewsRoom post (and of its contents) to holders of
`content.unit.assign`, which is meant for `elsa-workflows` alone. Posts without a NewsRoom project are assigned by hand
as before. The workflows wait `Elsa:Newsroom:ReviewerDelay` (one delay for every Section; the Sections' own assignment
delay is no longer read) after a submission, or after Content returns a review to the queue, then accept the post and
assign the least loaded holder of the role checked in to a running shift of the post's unit, never the post's owner.

- **Content's setting on, `ReviewerRole` empty** (the default): nobody who lacks `content.unit.assign` can give a
  NewsRoom post a reviewer, so such posts stop at the desk.
- **`ReviewerRole` set, Content's setting off** (Content's default): Content refuses the workflow's accept and
  assignment, since `elsa-workflows` does not hold the manual rights. It logs a warning and tries again; people can
  assign by hand.
- **Both off:** people assign reviews. The activities that assign contents (**Assign in shift**, **Work in shift**,
  so the default `newsroom-content`) then need `content.assign` granted to `elsa-workflows` as well.
- **A NewsRoom post that no `DeskReview` with a `ReviewerRoleKey` runs for** (no binding, a workflow that ended or was
  cancelled, or an authored definition without one) gets no reviewer from the workflows.

Reviewers approve with their own rights, and Content has DynamicFields check the post as them: besides the shift's
`content.review` they need `dynamicfields.read`, or the approval answers `503`.

The module's default for `ReviewerRole` stays empty, to match Content's default: role keys and the moment to switch
belong to the deployment.

## Grants

Tenant-wide, for the service subject `elsa-workflows`. They are tenant-scoped and must be provisioned for each tenant, new ones included (`POST /api/identity/grants` by someone with `access.grants.manage`); do not add them to the client token as global permissions.

| Permission | Used for |
|---|---|
| `content.unit.assign` | filing a post in its Section and a content in its unit; assigning contents, and accepting and assigning posts, where [Content's workflow owns assignment](#who-assigns); it is also what lets the service see posts |
| `content.publish` | **Publish post** (publish and schedule), and unscheduling the post of a killed project |
| `shifts.view` | the running shifts and their rotas |
| `newsroom.workflow` | reading projects and reporting their states; bindings, routing and workload |
| `org.read` | Organization's members of a unit, to find its managers when a deadline passes and no lead is on shift |
| `dynamicfields.read` | Content's publish has DynamicFields check the post as the caller; without it publishing answers `503` |

A missing grant does not fault a newsroom workflow: the activity logs a warning, tries again, and goes on once the grant is there.

Earlier versions also needed `content.assign`, `content.transfer` and `shifts.manage`: revoke them after the upgrade.
Keep `content.assign` only where Content's workflow does not own assignment (see [Who assigns](#who-assigns)).

## Newsroom workflows

NewsRoom owns post projects; Elsa runs the workflow of each.

### Events

The module subscribes to these integration events through the platform's inbox (tables `elsa_events.integration_inbox` and `integration_outbox` in each tenant's database; the migration is PostgreSQL only):

| Event | What Elsa does |
|---|---|
| `newsroom.project-approved.v1`, `newsroom.project-binding-available.v1` | starts the bound workflow, one instance per tenant and project |
| `newsroom.project-reapproved.v1` | starts it if the project has none, else wakes the waits |
| `newsroom.project-state-changed.v1` with `Killed` | cancels the project's workflows, sub-workflows included, then [unschedules its post](#scheduled-publishing) |
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
| `newsroom-post` **2** (latest) | files the post in its Section (a post the Section cannot take waits for the moderator) → produces its contents → desk review (a returned post is produced again) → **waits until the desk publishes the post** in Content, now or on a schedule → tells the author. It never publishes by itself. |
| `newsroom-post` 1 | the same up to the desk review, then **publishes the approved post itself** (a refused publication tells the proposal's author and goes back to the desk) → tells the author |
| `newsroom-content` | gives the content to the unit its kind is routed to → has the people on shift there produce it until it is accepted |

- **Which version of `newsroom-post`.** A NewsRoom binding without a version runs the **latest published** one, so
  once a deployment has version 2, **new projects under such bindings no longer publish on a reviewer's approval**.
  Pin the binding to version 1 to keep that. A running instance stays on the version it started with. A start that was
  delayed (a binding added later, a project approved again that has no instance yet) takes the latest then.
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
| `WaitForPublication` | `EndOnRefiling` | `Published`, `BackToDesk`, `Refiled` | |
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
- **Killing** a project cancels its instances. Until the kill's event arrives, their waits do nothing.
- **Loops.** In a flowchart, a step may lead back to an earlier activity. Elsa 3.9.0 forgets, once an activity has
  waited, that it was reached by such a step; a later loop then ends the run, "Finished", without an error. The
  activities above keep that mark themselves. **Elsa's own activities that wait (a delay, an event) do not**: test every
  loop of an authored definition that passes through one, twice.

### Publishing and the desk

- **A schedule taken back is the desk's.** Content shows an unscheduled post as plain "Approved". `DeskReview` and
  `PublishPost` read the post's history: once a schedule was taken back, and until a reviewer approves the post again,
  they neither publish nor schedule it. The desk publishes it, schedules it or returns it in Content. This holds for
  version 1 too, also for runs that were already waiting, and for a definition whose `PublishPost` has a `PublishAt`.
- **`PublishPost` only publishes an approved post.** When the post is returned, edited by the desk or unscheduled, it
  ends with `Refused` without asking Content (`Reason` says why), and the `Notify` of type
  `workflow.publish-refused` that follows sends nothing. A publication that Content refuses while the post changes is
  not a refusal either: the activity looks again. In `newsroom-post` 1 the run then waits in the desk review.
- **Who publishes by hand** (always in version 2) needs `content.publish` on the post and, as Content has DynamicFields
  check the post as them, `dynamicfields.read`. Content's `WorkflowOwnsAssignment` does not touch publishing or
  scheduling. Someone who wrote or reviewed the post also needs `content.editor` on its categories.
  Returning a post the workflow works on takes more: its assigned reviewer (`content.review`), `content.return` or
  `content.unit.assign`. Give the desk's shift role `content.publish` and `content.return`.

### Handover in a workflow

- **Content.** `WorkInShift` remembers the shift in which the assignee holds the work. When that shift has ended, it
  assigns the content to the least loaded holder of the role on a running shift, unless the assignee goes on in one.
  Someone it never saw on a shift of the unit was assigned by hand and keeps the work.
- **Reviews** are Content's to hand over: it returns to the queue a review whose reviewer has had no right to review
  the post for `Content:Newsroom:ReviewHandoverGrace`. A `DeskReview` with a reviewer role then assigns it again, after
  `ReviewerDelay`.
- **New reviewers:** see [Who assigns](#who-assigns).

### Scheduled publishing

`PublishPost` with `PublishAt` schedules the post and waits; `WaitForPublication` waits for a schedule the desk made.
Either way Content publishes the post when it is due; Elsa does not ask it to.

When a project is killed, Elsa reads its post and, if it is scheduled (by a workflow or by hand, with or without a
workflow), takes the schedule back. If Content does not take the call, the kill's event fails and the host delivers it
again; a delivery that is given up on, or a post that falls due before the call gets through, leaves the post to be
published.

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
| `Elsa:Newsroom:ReviewerRole` (see [Who assigns](#who-assigns)) | empty |
| `Elsa:Newsroom:ReviewerDelay` | `00:05:00` |
| `Elsa:Newsroom:RecheckInterval` | `00:05:00` |
| `Elsa:Newsroom:SettleDelay` (the extra look right after a wait begins) | `00:00:02` |
| `Elsa:Newsroom:RetryDelay` (after a conflict, or a module that did not answer) | `00:00:30` |
| `Elsa:Newsroom:TimerSweepInterval` | `00:01:00` |
| `Elsa:Newsroom:TimerSweepGrace` (how overdue a timer is before the sweep takes it) | `00:00:30` |

`Elsa:PollInterval` and `Elsa:HandoverLookback` no longer exist: the coordinator that read them is gone (see
[Upgrading from the coordinator](#upgrading-from-the-coordinator)). `Elsa:Newsroom:ProjectWorkflow` no longer exists: bind post kinds in NewsRoom instead. Workflows that an earlier version
left suspended go on after the upgrade. A `Rules` input of `RouteToUnit` in a saved definition is ignored: the rules are
NewsRoom's now.

## Upgrading from the coordinator

Earlier versions ran a poller next to the workflows. Its jobs are now:

| Job | Now |
|---|---|
| publishing due posts | Content, by itself |
| returning the unfinished reviews of ended shifts | Content, by itself, after its grace |
| assigning reviewers of accepted posts, after each Section's delay | `DeskReview` for NewsRoom posts, with one delay ([Who assigns](#who-assigns)); people for the others |

**Order.** Grant `shifts.view` ([Grants](#grants)), upgrade Content, then upgrade Elsa in the same window. The old
poller reads Content's removed `/automation/accepted` first and gives up on its `404`, so against the new Content it
should do none of its jobs, publishing included (read from its code, not run).

**Posts that are already scheduled.** The new Content publishes at the due time under the URL prefix it saves when a
post is scheduled. Posts scheduled before its upgrade have none, and stay `Scheduled`:

1. Before upgrading, list the scheduled posts of every tenant.
2. After upgrading Content, publish each one now or schedule it again; or, once all of them are due, call
   `POST /api/content/posts/publish-due` once, with tenant-wide `content.publish` and `dynamicfields.read`.
3. Check Content's log for `will NOT be published automatically`.

Content's E3 deploy notes say how to list them.

**Workflows that are waiting go on.** The default workflows' definitions did not change (`newsroom-post` and
`newsroom-content` stay at version 1, so there is no new class): what changed is what two activities do while they wait,
and instances resume on the new code. A `PublishPost` waiting on a scheduled post no longer asks Content to publish it.
A `DeskReview` no longer transfers a review whose shift ended; Content returns it, and the activity assigns it again.

## Upgrading to `newsroom-post` version 2

- **Decide the version first.** A binding of `newsroom-post` without a version moves new projects to version 2 as soon as
  the module is upgraded: the desk then has to use **Publish now** or **Schedule** in Content, so make sure the desk's
  people hold the rights above. To keep version 1, set the binding's version to 1 before upgrading. Pinning 2 before
  the upgrade stops starts until the upgrade (no such definition yet; the event is delivered again).
- **No grant, setting or database change.** Content must be one that returns the post's history (every version does).
- **Waiting runs** stay on version 1 and pick up the changes to the desk review and publication.
- **Runs that the loop fault already touched** are not repaired: a run that was already waiting after a backward step
  (producing a returned post again, or at the desk after a refused publication) was marked before the upgrade, and a
  later refiling or return may still end it. Such a project stays open in NewsRoom with a finished workflow; publish it
  by hand in Content.

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
