# Robot dispatch API: concurrency and distribution (SIT315 M4.T1D)

Manit Khera | s224520987 | SIT315 Concurrent and Distributed Programming | M4.T1D

This repository takes the robot dispatch path of my SIT331 backend (ASP.NET Core 8 over PostgreSQL) and makes it
faster under load and able to scale out across several API replicas. The SIT331 project's own README is kept as
[`SIT331_README.md`](SIT331_README.md).

The full write-up is [`report/M4T1D_report.pdf`](report/M4T1D_report.pdf).

## Contents

| Path | What it is |
|---|---|
| `BoundedContexts/`, `Infrastructure/`, `Program.cs` | the API |
| `tests/` | 54 automated tests, run against a real PostgreSQL in Docker |
| `loadgen/RobotLoadGen/` | the load generator: a simulated robot fleet written with NBomber |
| `loadgen/azure/` | the Azure setup and benchmark scripts used for every reported run |
| `loadgen/results/` | raw results of every run |
| `report/M4T1D_report.pdf` | the report |
| `visuals/` | two interactive HTML pages explaining the dispatch path (see below) |

## Branches: what came from SIT331 and what was added in SIT315

| Branch | What it holds |
|---|---|
| `main` (default) and `new` | the final SIT315 code (the two are identical) |
| `old` | the SIT331 code exactly as benchmarked |
| `phase-1` to `phase-4` | the step by step changes, each built on the one before |
| `loadgen` | the load generator, added between phase 3 and phase 4 |

The tag `sit331-baseline` marks the last SIT331 commit. `git diff old..new` is the SIT315 work.

## The workload

A robot runs a loop of three HTTP requests per job:

```
POST  /api/adapter/devices/{id}/work-items/claim-next   -> 200 job | 204 no work | 429 come back later
PATCH /api/adapter/jobs/{jobId}/started
PATCH /api/adapter/jobs/{jobId}/completed
```

The SIT331 version works for a handful of robots. Under load it slows down, and at 5,000 robots it stops completing
jobs at all.

## What was wrong with the old dispatch path

| # | Problem | Kind |
|---|---|---|
| F1 | claim was a read then a separate write, so two robots could claim the same job | correctness |
| F2 | started, completed and failed overwrote the job without checking its state | correctness |
| F3 | authentication loaded every device credential, then searched the list in C# | DB trips + API CPU |
| F4 | authentication wrote LastUsed back to the database on every request | extra DB write |
| F5 | every claim-next scanned the robot's whole job history for stale work | extra DB work |
| F6 | the stale sweep loaded every job, and every replica would run its own sweep | DB work, replica conflict |
| F7 | all database I/O on the dispatch path was synchronous | threads blocked on every query |
| F8 | no limit on how many requests are admitted | no backpressure |
| F9 | a second ownership query repeated a check already done | extra DB trip |
| F10 | an unused StopWithoutWork decision | dead code |
| F11 | the claim query read all of a robot's queued jobs to keep the oldest one | expensive query |
| F12 | every SQL statement was parsed and planned again on every execution | DB CPU per query |

Grouped by effect that is five bottlenecks: too many database trips per request, each trip too expensive, threads
asleep while the database works, no admission control, and the fixed capacity of one API process.

## How the optimisations were made

The work went in as four phases, one branch each, every phase tested before the next.

**Phase 1: correctness and fewer queries (F1 to F5, F9, F10).** The claim became one guarded statement. Only one
caller can match `status = 'Queued'`; everyone else gets zero rows and moves to the next job, so a job can never be
claimed twice:

```sql
UPDATE public.job
SET status = 'Claimed', claimedbydevicecredentialid = @deviceCredentialId, claimedatutc = @claimedAtUtc, ...
WHERE id = @id AND status = 'Queued'
RETURNING ...;
```

started, completed and failed carry the same guard on the expected state and the robot's `claimedAtUtc`.
Authentication fetches one credential row by id and writes LastUsed at most once every 10 minutes.

**Phase 2: one stale sweep across all replicas (F5, F6).** Stale work moved out of claim-next into a background
service that runs every 15 s with one targeted query. Each replica runs the service, but the sweep sits inside a
transaction scoped PostgreSQL advisory lock (`pg_try_advisory_xact_lock`), so only one replica sweeps per tick and a
crashed replica cannot hold the lock.

**Phase 3: async I/O and backpressure (F7, F8).** Every database call on the robot endpoints is `async`. The thread
goes back to the pool while PostgreSQL works:

```
sync  (old):  thread T1: [parse][== blocked on PostgreSQL ==][reply]
async (new):  thread T1: [parse]   serves other requests     [reply]   (may be a different thread)
```

The endpoints share one concurrency limiter (`Infrastructure/DispatchBackpressure.cs`): 64 requests run per replica,
32 wait in arrival order, and the rest get 429 with `Retry-After: 1` straight away. The limiter runs before
authentication, so a rejected request costs no database work. It caps work in progress, not requests per second.

**Phase 4: cheaper queries (F11, F12).** The claim reads the oldest queued job through an index with `LIMIT 1`, and
Npgsql prepared statements are on (`Max Auto Prepare = 64`), so a repeated statement is planned once per connection.

**Distribution.** The API keeps no state between requests, so replicas need no coordination apart from the sweep lock.
Each replica has its own limiter and connection pool, a load balancer spreads robots across them, and they share one
PostgreSQL.

```
                         ┌──► API replica 1 (2 vCPU) ──┐
robots (load VM) ──► LB ─┤                             ├──► one PostgreSQL (8 vCore)
                         └──► API replica 2 (2 vCPU) ──┘
```

## The experiment

All reported numbers were measured on Microsoft Azure, Korea Central, on 2026-10-04.

| Part | Specification |
|---|---|
| API VM 1 | Standard_D2s_v4, 2 vCPU, 7.8 GB, Intel Xeon Platinum 8370C @ 2.80 GHz, Docker |
| API VM 2 | Standard_D2s_v4, 2 vCPU, 7.8 GB, Intel Xeon Platinum 8272CL @ 2.60 GHz, Docker |
| Load VM | Standard_D2ds_v4, 2 vCPU, 7.8 GB, Intel Xeon Platinum 8272CL @ 2.60 GHz |
| Database | Azure Database for PostgreSQL Flexible Server 18, Standard_D8ds_v5, 8 vCore, 128 GB |
| Load balancer | Azure Standard internal load balancer, HTTP health probe on /health every 5 s |
| Connection pool | Npgsql Maximum Pool Size 25 per replica, then repeated with 50 |

Method:

- Closed loop load with NBomber: each robot sends its next request as soon as the previous answer arrives. On 429 it
  waits Retry-After and tries again.
- Every robot starts with a backlog of 2,000 queued jobs, re-seeded before each run.
- Sizes 10, 100 and 400 robots; stress at 2,000 and 5,000 robots.
- Setups: old and new, each with 1 and 2 API replicas, at pool 25 and pool 50.
- 30 s measured after a 5 s warm-up, 3 repeats per point, median reported. 72 grid runs and 8 stress runs.
- After every run the load generator checks the database: completed jobs must equal history rows, and nothing may end
  failed or in an unknown state. All 72 grid runs passed.
- Speedup is jobs per second against old with 1 API at the same size. In a closed loop that equals the reduction in
  time per job (time per job = robots / jobs per second).

## Findings

Throughput at pool 25, median of 3 (jobs/s):

| robots | Old, 1 API | Old, 2 APIs | New, 1 API | New, 2 APIs |
|---|---|---|---|---|
| 10 | 179.7 | 168.7 | 341.5 | 332.2 |
| 100 | 246.1 | 363.9 | 617.9 | 1,102.9 |
| 400 | 176.7 | 284.2 | 607.7 | 1,069.0 |

Speedup over old with 1 API at 400 robots: old with 2 APIs 1.61x, new with 1 API 3.44x, new with 2 APIs 6.05x.
Time per job falls from 2,263 ms to 374 ms.

1. **Light load is about work per request.** At 10 robots nothing queues, so the 1.90x comes from fewer and cheaper
   database trips. Claim p50 falls from 24.0 ms to 9.7 ms. A second replica gives nothing here (0.94x old, 0.97x new)
   because one API is not busy.
2. **Under load the old code gets worse, not just flat.** Old with 1 API drops from 246.1 jobs/s at 100 robots to
   176.7 at 400 and its claim p50 climbs 24.0 -> 132.5 -> 766.5 ms. Every thread sits blocked on PostgreSQL and every
   request is admitted. New holds a flat claim p50 (58.9 ms at 100 robots, 59.5 ms at 400) because async frees the
   thread and the limiter keeps at most 96 requests inside each replica.
3. **Latency alone overstates the gain.** At 400 robots new's claim p50 is 12.9x lower than old's, but robots finish
   jobs 3.44x faster. Latency only covers requests that got in; the 429 waits sit outside it. Time per job is the
   speedup used.
4. **Scaling out works once the API is the limit.** A second replica gives new 1.78x at 100 robots and 1.76x at 400,
   and old 1.48x and 1.61x. It stays under 2x because both replicas share one database.
5. **The connection pool became new's queue.** Raising the pool from 25 to 50 made new 16 to 22 % faster at 100 and
   400 robots and did nothing clear for old (-4 % to +10 %, inside its own run spread). With async code a request
   waiting for a connection holds no thread, so the pool is where new's requests wait. With pool 50 new with 2 APIs
   reaches 6.57x. The gain from the second API also drops from 1.76x to 1.68x, a first sign of the shared database
   becoming the limit.
6. **Stress test: old collapses.** At 5,000 robots old with 1 API completed 0.0 jobs/s in the window, with 2,881
   failed requests, claim p95 of 59,376 ms and 9,342 jobs left mid flight. Pool 50 did not rescue it (6.9 jobs/s).
   New with 2 APIs held 1,007.8 jobs/s (1,174.5 at pool 50), answering 146,172 requests with 429 and failing none.
   Its breaking point was not reached because one 2 vCPU load VM cannot drive more.

| Bottleneck | Fix | Where it shows |
|---|---|---|
| too many DB trips | F3, F4, F5, F9 | 1.90x at 10 robots |
| trips too expensive | F11, F12 | the same, and less DB load per job when scaling out |
| threads asleep on DB | F7 async | old degrading from 100 to 400 robots, new flat |
| no admission control | F8 limiter | flat new latency with 429s, old collapse under stress |
| one API's capacity | second replica | 1.76x for new at 400 robots, nothing at 10 |
| pool size | config | +16 to +22 % for new, nothing clear for old |

## Raw results

Each run folder in `loadgen/results/` holds `run-info.txt` (hardware and parameters), `bench.csv` (one row per step
per run: counts, req/s, p50 to p99, status codes), `db_check.csv` (the database check after each run), the run log,
and the NBomber HTML/CSV/MD report per run under `nbomber/`.

| Folder | What it is |
|---|---|
| `azure-grid-20261004-033619` | main grid, pool 25 (36 runs) |
| `azure-pool50-grid-20261004-091319` | the same grid, pool 50 (36 runs) |
| `azure-stress-20261004-042132` | stress test, pool 25 |
| `azure-pool50-stress-20261004-094406` | stress test, pool 50 |
| `azure-video-20261004-040905` | load used while recording the video, not used in the report |
| `20261003-*`, `rehearsal-*`, `profile-*` | earlier local runs on a laptop while building the harness, not used in the report |

## How to run

Tests (Docker must be running; integration tests that need a live API are excluded):

```
dotnet test tests/RobotControllerApi.Tests/RobotControllerApi.Tests.csproj --filter "FullyQualifiedName!~Integration"
```

Images:

```
git archive old | docker build -t robot-api:old -
git archive new | docker build -t robot-api:new -
docker build -t robot-loadgen -f loadgen/Dockerfile .
```

Azure, from `loadgen/azure/` (needs the Azure CLI; `PGPASS`, `MYIP` and `KEYS` set in the environment):

| Script | Runs on | Does |
|---|---|---|
| `setup-1-network-db.sh` | your machine | resource group, network, PostgreSQL, container registry |
| `setup-2-vms-lb.sh` | your machine | NAT gateway, the load VM, two API VMs, internal load balancer |
| `api.sh old\|new\|stop [pool]` | each API VM | starts a version with a pool size, or stops it |
| `grid.sh` | load VM | the full grid; `POOL=50 ./grid.sh` for the second one |
| `stress.sh` | load VM | the stress runs |

Each benchmark step runs the load generator against the load balancer:

```
docker run --rm -v $OUT:/loadgen/results robot-loadgen bench --api http://10.10.1.100 --db "<connection string>" \
  --hash-key <key> --label new-2-replica --robots 10,100,400 --backlog 2000 --duration 30 --warmup 5 --repeats 3
```

## Visual explanations

Two self-contained HTML pages in `visuals/`. Open them in a browser.

- `pipeline_explorer.html` steps through claim, started and completed in the old and new code side by side, showing
  the real code and each database trip it makes.
- `dispatch_under_load.html` is an animated model of robots, threads, the limiter and the connection pool under load.
  It is a teaching model: its timings are slowed down and picked for clarity, not measured. The measured numbers are
  in the report and `loadgen/results/`.
