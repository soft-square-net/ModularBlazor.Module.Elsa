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
5. Grant service subject `elsa-workflows` tenant-wide `content.assign`, `content.transfer`, `content.publish`, and `shifts.manage` (`GET /api/shifts/overdue` requires it). These grants are tenant-scoped and must be provisioned for each tenant; do not add them to the client token as global permissions.
6. Ensure replicas share the module catalog/package storage and configure the Elsa database with durable storage/backups. PostgreSQL is the provider wired by this initial module.

Section settings control the assignment delay (default five minutes) and checkout grace (default fifteen minutes), after which Shifts checks the shift out. Assignment delay begins when the manager accepts the submission. Editing a live creative work retains its published snapshot and cancels a pending schedule pending Desk reapproval.

## Shift handover

For every shift that ended (checked out by a person or by `ShiftClock`), Elsa calls Content's handover, which returns the shift's unfinished review work to the queue.

- **Where it reads.** `GET /api/shifts/shifts/ended?since={utc}&take=500` (`shifts.manage`), oldest end first.
- **Cursor.** One per tenant, **in memory**: the newest `endedAt` seen. Each read goes back five minutes behind it, because a shift can be saved after one with a later end was read. Handled shifts are skipped by id.
- **Failures.** Only a successful handover counts. After a failed one (any other status, `404` and `409` included), the cursor stays at that shift and the next poll retries it; the other shifts are still handed over.
- **Restarts.** A new process looks back `Elsa:HandoverLookback` (default `1.00:00:00`, one day) and hands those shifts over again; the handover is idempotent. A shift that ended longer ago than that while Elsa was down, or whose handover was still failing at a restart, may not be retried. Write the value as `days.hours:minutes:seconds`: `24:00:00` means 24 days.
- **Replicas** each keep their own cursor and may repeat a handover.
- **Older Shifts.** When the ended list answers `404`, Elsa uses `GET /api/shifts/overdue` and calls the handover once the shift is past its end plus the checkout grace. That list holds only shifts not checked out yet, so a shift checked out before Elsa's next poll past the grace gets no handover there.

The current coordinator reconciles persisted business deadlines on a configurable polling interval. The Elsa engine, persistence, tenant resolver, and secured Elsa API are hosted by this module. Authorable Elsa workflow definitions for these operations are a follow-up; keep business state transitions in Content/Shifts if that is added.
