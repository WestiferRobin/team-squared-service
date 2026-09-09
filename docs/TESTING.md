# Testing

[README](../README.md) · [Architecture](ARCHITECTURE.md) · [Development and environment setup](DEVELOPMENT.md)

For contributors choosing, writing or running tests, this guide explains what each
level proves and how to select existing helpers safely. Newcomers can start with
[commands](#commands) and the [first contribution exercise](DEVELOPMENT.md#first-exercise-two-host-configuration-isolation).
Reviewers can jump to [test placement](#test-placement), [helpers](#test-helpers)
and [infrastructure safety](#infrastructure-safety).

## Mental model

| Level | Meaning |
| --- | --- |
| UNIT | Isolated behavior, with deterministic doubles where needed |
| INTEGRATION | A real framework/provider boundary: HTTP hosting, configuration, EF/PostgreSQL or Redis |
| BOTH | Separate evidence is valuable at both levels |
| NO DIRECT TEST | Passive interfaces/generated artifacts whose consumers and schema tests prove behavior |

Test count and 100% line coverage are not the goal. A passing test should establish
an observable requirement that could fail independently of the test implementation.

## Source-first test design

**Production behavior → required cases → correct test level → implementation.**
Read the affected source and current tests before choosing a helper or adding cases.
For behavior that exists, consider success, failure, boundary, special cases, state
transitions, validation, serialization, cancellation, timeout, logging,
configuration and infrastructure. Do not manufacture a case in every category for
a passive type or duplicate the same evidence across levels.

For example, a service write can require evidence that persistence completes
before invalidation starts, that a persistence failure prevents invalidation, and
that committed-write invalidation uses its intended cancellation policy. Merely
recording that both methods were called does not establish completion ordering.
A HTTP-only formatting change needs public JSON evidence; it does not inherently
need a new PostgreSQL test or migration.

## Test placement

Production paths are relative to `src/Service.Api/`. Test projects are
[Service.Api.UnitTests](../tests/Service.Api.UnitTests) and
[Service.Api.IntegrationTests](../tests/Service.Api.IntegrationTests); match the
existing feature folders rather than creating another test project.

| Subject | Established level | Evidence / placement |
| --- | --- | --- |
| DTO / Request | UNIT | Validation/serialization in unit `Dtos/`; HTTP binding indirectly in integration `Controllers/` |
| Enum | UNIT | Meaningful values/validation in unit `Enums/`; public representation indirectly in HTTP/OpenAPI tests where relevant |
| Model | UNIT + INTEGRATION | Meaningful defaults in unit `Models/`; persistence in integration `Infrastructure/Database/` |
| Mapper | UNIT | DTO → Response fields in unit `Mappers/` |
| Service | UNIT | Validation, orchestration, mapping and cache policy in unit `Services/`; HTTP flows exercise services indirectly |
| Controller | BOTH | Delegation/results in unit `Controllers/`; routing/binding/JSON/status/headers in integration `Controllers/` |
| ApiExceptionHandler | BOTH | Exception classification/logging in unit `Exceptions/`; hosted ProblemDetails/error behavior in integration `Exceptions/` |
| CacheOptions | BOTH | Validation in unit `Infrastructure/Cache/`; binding and startup validation in integration `Startup/` |
| RedisCache | BOTH | Timeout/cancellation/failure policy in unit `Infrastructure/Cache/`; real TTL, serialization and recovery in integration `Infrastructure/Cache/` |
| ItemCache / ActionCache | BOTH | Resource keys/TTL/delegation in unit `Infrastructure/Cache/`; real roundtrips/isolation and HTTP cache behavior in integration `Infrastructure/Cache/` and `Controllers/` |
| Repository | INTEGRATION | Real queries, writes, tracking, constraints and races in `Infrastructure/Database/Repositories/` |
| ServiceDbContext | INTEGRATION | Save/timestamp behavior and persistence in `Infrastructure/Database/` |
| EF Configuration | INTEGRATION | Actual schema constraints and persistence, not just reading configuration code |
| Migration | INTEGRATION | Clean application, current state, rollback/reapplication and model agreement in `Infrastructure/Database/MigrationTests.cs` |
| Health check | INTEGRATION | Real dependency/schema states and HTTP readiness/liveness in `Health/` |
| Extensions | INTEGRATION | Registration, options and provider/host behavior in `Startup/` and relevant infrastructure tests |
| Program / configuration | INTEGRATION | Actual hosting, middleware, endpoints and configuration through `Startup/`, `Controllers/`, `Exceptions/`, `OpenApi/` and `Health/` |
| Interfaces | NO DIRECT TEST | Test implementations and consumers |
| Generated migration designer / snapshot | NO DIRECT TEST | Verify through migration/model agreement and schema tests; review generated diffs |

**BOTH does not mean duplicate assertions.** A controller unit test proves service
delegation/result construction without ASP.NET hosting; a HTTP integration test
proves routing, binding, JSON, status and headers. RedisCache unit tests use controlled
provider outcomes to prove timeout/cancellation/failure policy; real Redis tests
prove provider TTL, serialization and recovery. Use each boundary for its own evidence.

## Naming and deterministic unit tests

Use `Method_WhenCondition_ExpectedResult`; for cross-method behavior use
`Behavior_WhenCondition_ExpectedResult`. Illustrative names, not a list of existing tests:

```text
GetAsync_WhenCacheMisses_LoadsRepository
CreateAsync_WhenParentIsMissing_ThrowsItemNotFound
CheckHealthAsync_WhenDatabaseIsUnavailable_ReturnsUnhealthy
WriteLifecycle_WhenPersistenceIsPending_DoesNotInvalidateCache
```

Unit tests must not require PostgreSQL, Redis, Docker, WebApplicationFactory or
network access. Use deterministic doubles and meaningful public outcomes. For
async orchestration, hold a dependency operation pending, await its started signal,
assert the service operation has not completed and later work has not started,
then release it and verify the result. Exercise failure/cancellation where the
source supports them. Bound waits so regressions fail instead of hanging; avoid
arbitrary sleeps or assertions based only on invocation order.

## Integration rules

Integration tests may use WebApplicationFactory, real PostgreSQL/Redis, EF migrations,
actual configuration and HTTP requests. Do not mock the boundary being certified:
a mocked repository cannot prove a PostgreSQL constraint, and direct controller
calls cannot prove HTTP routing or JSON binding. Substitute unrelated dependencies
only when that keeps the intended boundary real and clear.

Not every integration test needs Docker. Hosted binding and configuration cases can
run without providers; tests marked `Category=Postgres` or `Category=Redis` require
the corresponding dedicated TEST connection. Missing infrastructure is a setup
failure, not a reason to silently skip provider evidence.

## Test helpers

Follow each linked helper and its callers before extending it. Keep test mechanisms
out of production registration and choose the smallest helper that proves the behavior.

| Helper / source | What it does | When to use it | When not to use it |
| --- | --- | --- | --- |
| [ApiFactory](../tests/Service.Api.IntegrationTests/Fixtures/ApiFactory.cs) | Ordinary WebApplicationFactory host in Testing; clears inherited configuration sources, loads base JSON, disables OpenAPI and supplies a unique cache prefix; explicit host overrides can follow | Hosted HTTP/DI tests with deliberate per-host dependency overrides | Unit tests or checking environment-specific JSON defaults; it does not provision PostgreSQL/Redis |
| [ConfigurationApiFactory](../tests/Service.Api.IntegrationTests/Fixtures/ConfigurationApiFactory.cs) | Loads base and selected environment JSON without shell/command-line/user-secret configuration, then explicit overrides; default environment is Production | Environment defaults, OpenAPI gates, options binding and invalid startup configuration | Ordinary CRUD setup, or claims that the factory proves ambient shell precedence |
| [PostgresFixture](../tests/Service.Api.IntegrationTests/Fixtures/PostgresFixture.cs) | Requires a connection targeting `service_test`; creates/migrates a generated `service_test_<guid>` database and a host, then disposes/deletes its owned database | Real PostgreSQL repository, persistence and HTTP cases | LOCAL/DEV connections, starting Docker, or assuming Redis is enabled: its host explicitly sets Redis empty unless a test overrides it |
| [PostgresOperations](../tests/Service.Api.IntegrationTests/Fixtures/PostgresOperations.cs) | Creates fixture-connected contexts and observes real PostgreSQL lock waits with bounded polling | Transaction, locking and cancellation tests requiring an observed database state | General HTTP setup or replacing real locking evidence with a guessed delay |
| [RedisProxy](../tests/Service.Api.IntegrationTests/Fixtures/RedisProxy.cs) | Owned TCP proxy to real Redis with response pause/disconnect and forwarding signals | Real transport failures, in-flight operations and recovery | A fake Redis server, unit tests, or faults against resources the test does not own |
| [RecordingLoggerProvider](../tests/Service.Api.IntegrationTests/Fixtures/RecordingLoggerProvider.cs) | Captures messages/structured entries for selected API/service/framework categories; accepts additional categories | Hosted logging assertions, event ownership and sensitive-data checks | Assuming all categories are recorded by default; add a new resource's category explicitly |
| [RecordingCache](../tests/Service.Api.UnitTests/Infrastructure/Cache/RecordingCache.cs) | ICache double recording operation, key, type, value, TTL and token; supplies result/failure | Domain-cache wrapper delegation/key/TTL unit tests | Real Redis serialization/expiry/provider behavior |
| [PendingCall](../tests/Service.Api.UnitTests/Services/PendingCall.cs) | Controlled async operation with started signal and explicit completion/failure; supports cancellation and releases pending work on disposal | Service completion ordering and failure/cancellation unit tests | Real database locks or provider timing certification |
| [FakeDistributedCache](../tests/Service.Api.UnitTests/Infrastructure/Cache/FakeDistributedCache.cs) | Controlled distributed-cache payload, failure, pending operation, signals and recorded calls/options | RedisCache adapter policy without network access | Proving that Redis itself serializes, expires or recovers correctly |
| [RecordingServices.cs](../tests/Service.Api.UnitTests/Controllers/RecordingServices.cs) | Contains RecordingItemService and RecordingActionService, recording calls/arguments/tokens and returning configured results/failures | Controller delegation/result unit tests | A class named RecordingServices, or evidence of real service orchestration/persistence |

## Infrastructure safety

- PostgreSQL tests use dedicated TEST infrastructure. PostgresFixture requires the base database name `service_test` and creates generated databases for cases; never reuse LOCAL/DEV databases or delete their volumes. The dedicated user must be able to create/drop its owned test databases.
- Redis tests use unique prefixes/keys and targeted cleanup. Never use `FLUSHALL` or `FLUSHDB`; remove only keys owned by the test, even on failure.
- Dispose factories, connections, transactions, proxies and pending operations. Fault only owned resources; do not stop a shared Redis/PostgreSQL server to simulate an outage.
- Use bounded waits and deterministic signals. Observe the relevant operation/lock before triggering cancellation or recovery rather than relying on scheduler speed.
- Avoid process-global environment mutation. Use factory configuration overrides and scoped child-process environments so parallel tests do not reconfigure each other.
- Do not mix LOCAL/DEV/TEST ports, database names or volumes. The normal scripts create unique disposable Compose projects and clean only their resources. Follow [operational TEST guidance](DEVELOPMENT.md#test-overview) for credentials and failure cleanup.

## Commands

All commands run from the repository root in the [supported shell/setup](DEVELOPMENT.md#shell-and-working-directory).
Use SDK 8.0.303. Ordinary unit/hosted tests need the SDK and package restore; provider
runs also need Docker/Compose or independently provisioned dedicated TEST providers.
Smoke/certification additionally need Python 3. Leave `SERVICE_WORKFLOW_CERTIFICATION`
unset for normal runs.

| Purpose | Command | What it proves |
| --- | --- | --- |
| Fast unit feedback | `dotnet test tests/Service.Api.UnitTests` | Isolated behavior; no running infrastructure |
| Hosted feedback without PostgreSQL/Redis cases | `dotnet test Service.sln --filter "Category!=Postgres&Category!=Redis"` | Unit plus provider-independent hosted/configuration behavior |
| Integration project, with prerequisites below | `dotnet test tests/Service.Api.IntegrationTests` | Hosted HTTP/configuration plus real provider boundaries; does not provision them |
| Full automated suite | `./scripts/test.sh` | Restores, provisions isolated TEST PostgreSQL/Redis, runs the solution and cleans up |
| Actual image smoke | `./scripts/smoke.sh` | Builds/runs the root Dockerfile image in Staging; real HTTP CRUD/cache/cascade, health and Swagger, with owned resource cleanup |
| LOCAL/DEV workflow certification | `python3 scripts/certify-workflows.py` | Actual LOCAL and DEV topologies, persistence through recreation, isolation, script failure/interruption status/logging and cleanup |

Use `--filter FullyQualifiedName~YourTestName` with a `dotnet test` project command
for a focused regression. Filters do not supply missing infrastructure. The scripts
are the normal full-suite/image entry points; do not assume they forward arbitrary
`dotnet test` arguments.

### Direct integration prerequisites

Prefer `./scripts/test.sh` for provision/run/cleanup in one command. For direct
integration execution or IDE debugging, provision **dedicated TEST** PostgreSQL
and Redis first, then export `ConnectionStrings__Postgres` (base database exactly
`service_test`, user able to create databases) and `ConnectionStrings__Redis` into
the test process. PostgresFixture creates/migrates case databases; no LOCAL database
migration is needed. Ordinary ApiFactory does not import shell configuration, so
provider tests explicitly consume these connections through their fixtures/helpers.

The following optional Bash subshell gives a focused run its own TEST project and
ephemeral ports, without changing your parent shell's environment. It needs Docker
Compose v2 and uses disposable example credentials; stop on any provisioning failure.
For an IDE session, arrange equivalent dedicated resources and exports for the IDE,
retain them while debugging and clean only that session's project afterward.

```bash
(
  set -euo pipefail
  export POSTGRES_DB=service_test POSTGRES_USER=service POSTGRES_PASSWORD=change_me_test_only
  export POSTGRES_PORT=0 REDIS_PORT=0
  test_project="service-test-manual-$(uuidgen | tr '[:upper:]' '[:lower:]')"
  test_compose=(docker compose --env-file /dev/null -p "$test_project" -f docker/compose.test.yml)
  trap '"${test_compose[@]}" down -v --remove-orphans' EXIT
  "${test_compose[@]}" up -d --wait --wait-timeout 60
  test_postgres_port=$("${test_compose[@]}" port postgres 5432)
  test_redis_port=$("${test_compose[@]}" port redis 6379)
  export ConnectionStrings__Postgres="Host=127.0.0.1;Port=${test_postgres_port##*:};Database=service_test;Username=service;Password=${POSTGRES_PASSWORD}"
  export ConnectionStrings__Redis="127.0.0.1:${test_redis_port##*:}"
  dotnet test tests/Service.Api.IntegrationTests
)
```

This optional recipe also requires host `uuidgen` and `tr`. Its `down -v` targets
only the randomly named TEST project created inside the block. Do not substitute
a LOCAL/DEV project name. The maintained scripts provide broader signal handling
and failure logs; use them for routine certification.

### Workflow certification scope

Workflow certification is **not the normal inner loop**. Run it when changing
workflow/environment infrastructure or before certifying template-level changes.
It uses actual LOCAL/DEV Compose files, checks persistent rows across recreation,
and keeps those workflows alive while verifying TEST/smoke normal, controlled
failure and SIGTERM cleanup. It refuses occupied documented ports rather than
stopping unrelated processes. It does not prove cleanup after an uncatchable kill.

See [workflow certification operations](DEVELOPMENT.md#workflow-certification) for
port prerequisites, checkpoints, exit statuses and retained artifacts. Use
[troubleshooting](DEVELOPMENT.md#troubleshooting) for setup failures and the
[contributor checklist](DEVELOPMENT.md#contributor-checklist) to choose checks based
on your change. Passing unit tests alone does not certify HTTP, schema or real Redis
behavior; passing smoke alone does not replace the suite's detailed cases.
