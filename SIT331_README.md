# RobotControllerApi

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-239120?logo=csharp&logoColor=white)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-4169E1?logo=postgresql&logoColor=white)
![License: MIT](https://img.shields.io/badge/License-MIT-green)

A hardware-neutral .NET 8 backend that orchestrates autonomous mobile robots over HTTP.

It was built as the "brain" for a smart clothesline robot — an Arduino Nano 33 IoT robocar that
tracks the sun while laundry is drying and retreats to shade when it starts to rain. The API itself
knows nothing about pins, PWM, or motors. Robots **poll** it for work and report results back, so
the backend owns the modelling, validation, queuing, history, and access control while the firmware
stays simple.

> **Status: work in progress.** This is an active university/hobby project, not a finished product.
> The architecture and the HTTP surface are stable; test coverage is thin, some endpoints are still
> unauthenticated by design for local development, and parts of the hardware integration are built
> but untested. See [Known gaps](#known-gaps).

---

## Demo

The robot running against this backend:

[![Watch the demo](https://img.youtube.com/vi/ZW7Ps9AMmc0/hqdefault.jpg)](https://www.youtube.com/watch?v=ZW7Ps9AMmc0)

<https://www.youtube.com/watch?v=ZW7Ps9AMmc0>

---

## Why it's interesting

**Robots pull, the server never pushes.** Every robot polls a claim endpoint for its next work item.
There is no open socket to drop, so a robot that loses Wi-Fi mid-task simply stops asking — and the
server reclaims the work when its lease expires. This inversion is what makes the system tolerant of
flaky networks.

**Three interchangeable persistence layers.** Each bounded context declares a persistence *interface*
(`IDeviceDataAccess`, `IJobDataAccess`, …). Each interface has three complete implementations,
selected at startup from a single config value:

| `Persistence:Provider` | Implementation |
| --- | --- |
| `ADO` *(default)* | Hand-written Npgsql / raw SQL |
| `EF` / `EFCORE` | EF Core, `RobotContext` DbContext |
| `REPOSITORY` / `FASTMEMBER` | Generic repository using FastMember |

18 entities × 3 implementations. Changing one string in configuration swaps the entire data layer
with no change to any controller or service — the dependency inversion is load-bearing, not
decorative.

**Rollback by compensating transaction.** When a robot loses connection partway through a task, the
rollback engine reads the *immutable execution history* — what actually ran, never the original
request — maps each command to its inverse (`MOVE`↔`STEP_BACK`, `LEFT`↔`RIGHT`), reverses the order,
and queues the result as new work. It is the Saga pattern applied to a physical machine that has to
drive itself back to where it started.

**A grid pose you can't lie about.** The robot's position is treated as a trust state, not a sensor
reading. `PLACE x,y,facing` is the only source of truth. Manual driving, free-running, or an expired
in-flight job *invalidates* that trust, which blocks every grid-dependent command until the next
`PLACE`. Each claim is validated and simulated against the current pose before dispatch, so
out-of-bounds moves are rejected before the robot ever moves.

---

## Architecture

```
HTTP request
  └─ Controller       BoundedContexts/<Ctx>/Controllers/    routing, auth, HTTP status mapping
       └─ Service     BoundedContexts/<Ctx>/Services/       business rules, validation, orchestration
            └─ I<X>DataAccess   BoundedContexts/<Ctx>/Persistence/    interface only
                 └─ implementation   Infrastructure/DataAccess/{ADO,EFCore,Repository}/
                      └─ PostgreSQL
```

Controllers depend on service interfaces; services depend on data-access interfaces. Nothing above
the persistence layer references a concrete database implementation.

The code is organised into bounded contexts rather than by technical layer:

| Context | Responsibility |
| --- | --- |
| `AppUsers`, `Auth` | Human users, login, password hashing, claims, permission checks |
| `DeviceCredentials` | Robot adapter secrets — issue, revoke, HMAC-SHA256 hashing |
| `DevicePermissions` | Per-user, per-device permission tiers |
| `Devices`, `DeviceCapabilities` | Device records, map assignment, which commands a device supports |
| `CommandCatalogues` | Master command list (`AUTO`, `STOP`, `MOVE`, `PLACE`, …) |
| `Maps`, `DeviceStatuses` | Grid maps; live status, grid pose, heartbeats |
| `Telemetry` | Sensor ingest, latest/summary, condition classification |
| `Jobs` | One command = one job: create, validate, queue, dispatch to adapters |
| `Workflows` | Ordered multi-step sequences (`BestEffort` vs `AllOrNothing`) |
| `JobHistories`, `WorkflowHistories` | Immutable record of what actually executed |
| `Rollbacks` | Compensating work generated from successful history |
| `LiveControls` | Real-time manual control sessions and segments |

### Authentication

Two custom authentication schemes, hand-rolled rather than using ASP.NET Identity:

- **Basic** — for humans. Backed by `AppUsers` and a multi-algorithm password hash service.
- **DeviceCredential** — for robot adapters. Reads `X-Device-Credential-Id` and
  `X-Device-Credential-Secret`, verifies the hashed secret, checks active / not-revoked / not-expired,
  and enforces that the credential actually owns the `{deviceId}` in the route.

Authorization runs on named policies (`HumanUser`, `DeviceAdapter`, `AdminOnly`) plus a five-tier
per-device permission model: Viewer → Operator → Manager → Owner → Admin.

---

## Running it locally

**Requirements:** .NET 8 SDK, PostgreSQL 14+.

No secrets are stored in this repository. Configuration comes from
[dotnet user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) or environment
variables — the app will fail fast at startup with a clear message if a required key is missing.

```bash
git clone https://github.com/uiqvb/RobotControllerApi.git
cd RobotControllerApi

dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Host=localhost;Port=5432;Database=robotcontroller;Username=postgres;Password=<your-password>"
dotnet user-secrets set "DeviceCredential:HashKey" "<a-long-random-value>"

dotnet run
```

Then open `/swagger` for the API surface, or `/` for the built-in dashboard.

### Configuration keys

| Key | Default | Purpose |
| --- | --- | --- |
| `ConnectionStrings:DefaultConnection` | *required* | PostgreSQL connection string |
| `DeviceCredential:HashKey` | *required* | HMAC key for hashing device secrets |
| `Persistence:Provider` | `ADO` | Which data-access implementation to use |
| `WorkDispatch:LeaseMinutes` | `5` | How long a claimed work item stays leased |
| `WorkDispatch:MaxQueuedWorkItemsPerDevice` | `3` | Queue cap per device; also flood protection |

---

## API surface

Roughly 18 controllers. The main groups:

| Prefix | |
| --- | --- |
| `api/auth`, `api/users` | Registration, login, current user |
| `api/devices`, `api/device-capabilities`, `api/device-status` | Device registry and live state |
| `api/device-credentials`, `api/device-permissions` | Robot secrets and access tiers |
| `api/maps`, `api/command-catalogue` | Grid maps and the command master list |
| `api/jobs`, `api/workflows` | Creating and queuing work |
| `api/adapter/devices/{deviceId}/work-items` | **Robot-facing:** claim next, report started/completed/failed |
| `api/job-history`, `api/workflow-history` | What actually executed |
| `api/rollback-requests` | Requesting and auditing compensating work |

The core loop:

1. A user creates a job. `JobService` validates device, command, capability, and permission, then
   queues it.
2. The robot polls `POST /api/adapter/devices/{deviceId}/work-items/claim-next`. The dispatcher
   expires stale leases, walks the oldest queued jobs, validates each against the current grid pose,
   and returns the first one that is safe to run — marking it claimed with a lease.
3. The robot reports back started, then completed or failed. Results are written to job history and
   the device's pose is updated after a completed grid move.

`FEATURES_README.md` documents the endpoints in more detail.

---

## Known gaps

Being honest about what isn't done:

- **Test coverage is thin.** There is no automated test suite in this repository yet.
- **Some `GET` endpoints are anonymous** (job history, rollback requests) as a local-development
  convenience. They should be behind the `HumanUser` policy.
- **Stale work only drains on poll.** `ExpireStaleWork` runs inside the claim endpoint, so a robot
  that stops polling entirely never releases its lease, and the per-device queue cap then blocks new
  jobs. Needs a background timer or a manual reset endpoint.
- **No database migrations.** The schema is currently applied by hand; EF Core migrations are not
  wired up.
- **MQTT is deliberately not used.** HTTP polling was chosen for simplicity and network tolerance;
  a push transport is a possible future direction.
- **Hardware integration is partially untested.** The grid movement and live-control paths are built
  but have not been validated end to end against the physical robot.

---

## Tech

.NET 8 · ASP.NET Core Web API · C# · PostgreSQL · Npgsql · Entity Framework Core · FastMember ·
Swagger / OpenAPI · HMAC-SHA256

## License

See [LICENSE](LICENSE).
