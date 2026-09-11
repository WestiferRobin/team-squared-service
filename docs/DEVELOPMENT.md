# Development and operation

[README](../README.md) · [Architecture](ARCHITECTURE.md) · [Testing](TESTING.md)

## Shell and working directory

Run commands at the repository root. Normal workflows require Git, Docker with
Compose v2+, GNU Make 3.81+, and Bash on macOS/Linux. Docker must be running.
Host .NET is optional; host Python 3 is needed only by advanced verification.
Native PowerShell and BSD Make are not the supported interface.

## Normal Make workflow

```bash
make setup
make migrate
make run
make logs
make stop
```

ENV defaults to local for build/run/migrate/stop/logs. Unsupported values fail.
Setup verifies tools and creates only missing `.env.local`/`.env.dev`; it never
starts services or overwrites existing configuration. Docker image builds perform
SDK tool/package restore. Run does not migrate; first use and schema changes need
an explicit migration command.

| Command | Behavior |
| --- | --- |
| `make build ENV=local` | Build Dockerfile development target; no service startup |
| `make build ENV=dev` | Build Dockerfile runtime target; no service startup |
| `make run ENV=local\|dev` | Build/start selected API and dependencies; wait for Healthy readiness |
| `make migrate ENV=local\|dev` | Start selected PostgreSQL; SDK container applies existing EF migrations |
| `make stop ENV=local\|dev` | Compose down for selected project, preserving volumes |
| `make logs ENV=local\|dev` | Follow API logs; `LOGS_ALL=1` includes dependencies |
| `make unit` | Unit project in SDK container, no provider containers |
| `make integration` | Integration project with isolated real TEST providers |
| `make test` | Full solution with isolated real TEST providers |

The public Make targets are exactly help/setup/build/run/stop/logs/migrate/unit/
integration/test. Advanced scripts are intentionally separate.

## LOCAL

LOCAL is fully containerized: API in Development, PostgreSQL and Redis.
Default loopback bindings: API 5080, PostgreSQL 55432, Redis 56379. Database
`service_local`, cache prefix `service-local`, Compose project `service-local`.

The Dockerfile `development` target inherits the pinned SDK/tooling stage and
runs `dotnet watch --non-interactive --project src/Service.Api run --no-launch-profile`.
`src/` is mounted read-only; .NET artifacts output goes to a separate named volume
at `/artifacts`. No nested mounts or pre-existing host bin/obj directories are needed.
Polling file watching supports Docker Desktop; unsupported hot edits restart the
application. NuGet packages are restored into the image and reused through build
cache. Editing source does not write Linux build output into the host checkout.
Changes to Dockerfile, tool manifests, or dependency declarations warrant `make run`
to rebuild/recreate the container. No automatic IDE attachment is provided.

Normal stop preserves both PostgreSQL data and development build volumes. Use the
same project name when restarting. Ctrl-C during logs only stops the log follower.

## DEV

DEV builds the checked-out API source into the unchanged published runtime stage.
It runs as the image's non-root APP_UID, listens on container port 8080, has no
source mount, and uses Staging with `OpenApi__Enabled=true`.
Default loopback bindings: API 18080, PostgreSQL 25432, Redis 26379. Database
`service_dev`, cache prefix `service-dev`, Compose project `service-dev`.

```bash
make build ENV=dev
make migrate ENV=dev
make run ENV=dev
make logs ENV=dev
make stop ENV=dev
```

Both modes run locally. DEV is a built-image integration topology, not production
deployment. No source reload is promised in DEV; rerun `make run ENV=dev` to rebuild.

## Configuration sources

The helper parses `.env.local` or `.env.dev` as data, never executes it. Supported
keys: POSTGRES_USER, POSTGRES_PASSWORD, POSTGRES_DB, POSTGRES_PORT, REDIS_PORT,
API_PORT. Values are nonempty unquoted letters/digits/underscore/dot/dash; ports
must be 1–65535. Password is required. Defaults come from the selected topology.
Selected file/defaults override inherited shell values to prevent accidental
cross-environment connections. Files are ignored by Git and Docker build context.

Keep database names distinct. Compose project-scoped networks and volumes isolate
LOCAL/DEV even when logical container hostnames are both postgres/redis.
`API_PORT` is the published host port; the API always listens on container 8080.

`PROJECT=service-local-name` or `PROJECT=service-dev-name` selects another owned
standalone project. Reuse that value for every lifecycle command, choose unused
ports in the selected env file, and never target another user's resources.
The helper rejects unrelated project names. A project name does not resolve port
collisions by itself. Future parent composition owns its own project identity.

## Applying existing migrations

`make migrate ENV=local|dev` starts/waits for PostgreSQL only, builds the SDK tooling
image, and runs EF on the selected Compose network using internal postgres:5432.
It applies existing migrations; it never creates migrations. Failure propagates
through Docker/script as nonzero (GNU Make reports its normal recipe failure).
No host connection-string exports or host SDK are needed.

## Swagger and health

LOCAL and DEV enable Swagger. `/health` is liveness; `/ready` retains application
schema/database/cache readiness semantics. Run polls `/ready` from a temporary
SDK container on the selected network for up to 120 attempts with bounded HTTP
timeouts. Only HTTP success with body Healthy is accepted. Failure leaves runtime
containers for diagnosis and prints exact migration/log commands including PROJECT.
Do not assume every readiness error is a pending migration: inspect logs for
connection, schema, and Redis failures before taking corrective action.

## Debugging

Start LOCAL, edit source, and follow `make logs`. Watch applies supported edits or
restarts on unsupported edits. Compiler errors appear in logs and require a source
fix. Debugger attachment requires your IDE's explicit container process/source
mapping support and any debugger tooling it needs; it is not configured here.

For optional host debugging, install SDK 8.0.303 and restore tools/packages. Start
only PostgreSQL/Redis with raw Compose (below), supply host-mapped database/cache
connections to your IDE, and use the Service.Api launch profile on port 5080.
Stop the container API before binding that host port. Host debugging is an advanced
alternative, never the meaning of `make run ENV=local`.

## TEST overview

`make unit`, `make integration`, and `make test` build a fresh tooling image from
checked-out source. Unit uses a network-disabled tooling container with no
providers. Provider runs get unique `service-test-*` projects, database service_test,
real PostgreSQL/Redis with tmpfs storage, and ephemeral loopback ports. Tests connect
by container DNS, not the mapped host ports. Tests never reuse LOCAL/DEV volumes.
Existing fixtures/tests and counts remain authoritative; no test source is changed.

Tooling containers and per-run image tags are cleaned on success, error, INT and
TERM. Failure emits provider logs. Docker build cache may remain for faster reuse.
Uncatchable termination/daemon failure cannot guarantee cleanup; recover only the
reported owned project. See [Testing](TESTING.md) for test design and raw IDE testing.

## Advanced / IDE / raw tooling

These are not public Make targets:

```bash
./scripts/smoke.sh
python3 scripts/certify-workflows.py
```

### Workflow certification

Advanced verification requires host Python 3, but uses containerized SDK tooling.
Smoke proves the real published image's HTTP/Swagger/CRUD behavior with disposable
providers. Certification verifies LOCAL/DEV topology, CRUD/cache/cascade,
persistence through normal stop/recreation, and TEST/smoke success/failure/SIGTERM
cleanup while preserving unrelated resources. It refuses occupied standard ports.
Evidence lives under ignored `artifacts/workflows-<run-id>/`.

### Guarded reset

Only when intentionally deleting this selected standalone project's development
data, invoke the advanced helper. It prints the project and requires typing delete:

```bash
bash scripts/develop.sh reset local
```

For explicit noninteractive disposal of an owned throwaway project:

```bash
bash scripts/develop.sh reset local service-local-mycheck delete
```

This removes project volumes, including PostgreSQL data and LOCAL build caches.
It is irreversible. Reapply migrations afterward. No public reset target exists;
normal stop never deletes volumes.

### Raw container tooling

Compile without adding another public target:

```bash
docker build --target tooling -t service-tooling .
docker run --rm service-tooling dotnet build Service.sln --no-restore
```

For a focused unit test, run `dotnet test` with its project and filter in this same
image. For EF against a running standalone LOCAL database, supply its configured
credentials explicitly as environment and use `--network service-local_default`;
the connection host is postgres, port 5432. No Docker socket is mounted into tooling.
The reusable tooling command is `dotnet ef database update --project src/Service.Api`.

### Raw host / IDE tooling

Host SDK is optional and must match global.json. Direct EF authoring remains:

```bash
dotnet tool restore
dotnet restore
dotnet ef migrations add MeaningfulName --project src/Service.Api
```

Review generated migration/model changes using the contribution guidance below.
Host provider tests require dedicated TEST resources and host-mapped connections;
see Testing for the advanced recipe. Never point them at LOCAL/DEV databases.

Raw dependency-only startup (advanced host debugging):

```bash
docker compose --env-file .env.local -p service-local -f docker/compose.local.yml up -d --wait postgres redis
```

Raw Compose follows normal shell interpolation precedence; unlike the Make helper,
inherited exported values can override env-file settings. Do not mix workflows
without checking the selected project and resolved configuration.

## Future parent-repository reuse

This repository exports:

- Build context: repository root; Dockerfile targets tooling, development, runtime.
- Development: SDK/watch with source at /source/src; keep /artifacts container-owned.
- Runtime: non-root published Service.Api, container port 8080, no source mount.
- HTTP checks: /health and /ready; readiness success is HTTP 200 body Healthy.
- Configuration: ASPNETCORE_ENVIRONMENT, OpenApi__Enabled, ConnectionStrings__Postgres,
  ConnectionStrings__Redis, Cache__KeyPrefix; credentials supplied at runtime.
- Migrations: tooling target running `dotnet ef database update --project src/Service.Api`
  on the parent's service network with the parent's database connection.

A future parent may reuse these targets and service definitions under its own
network/project/configuration; it must not call standalone migration commands
against the wrong database. No parent repository implementation is included here.

## Troubleshooting

| Symptom | Action |
| --- | --- |
| Docker unavailable | Start Docker, then rerun setup |
| Unsupported ENV | Choose local or dev |
| Readiness fails | Inspect make logs, verify connections/schema/cache, explicitly migrate if needed |
| Port occupied | Stop your own conflicting environment or configure another host port |
| Source changes not visible | Check LOCAL bind mount/watch logs; rebuild after dependency changes |
| PostgreSQL data unexpectedly absent | Check ENV, PROJECT, POSTGRES_DB; do not reset as a repair shortcut |
| Certification port refusal | Stop only your own conflicting stacks before retrying |

## Where changes go

Paths below are relative to `src/Service.Api/` unless they start with `tests/`.
Item and Action are domain names; their persistence classes are **ItemModel** and
**ActionModel**, without aliases that disguise a differently named class.

| Change | Location / existing reference |
| --- | --- |
| Add/change endpoint, route, HTTP status | [Controllers/ItemsController.cs](../src/Service.Api/Controllers/ItemsController.cs) |
| HTTP input and input validation | [Dtos/Item/Requests/](../src/Service.Api/Dtos/Item/Requests) |
| Service/cache data | [Dtos/Item/ItemDto.cs](../src/Service.Api/Dtos/Item/ItemDto.cs) |
| HTTP output | [Dtos/Item/Responses/ItemResponse.cs](../src/Service.Api/Dtos/Item/Responses/ItemResponse.cs) |
| DTO → Response conversion | [Mappers/ItemMapper.cs](../src/Service.Api/Mappers/ItemMapper.cs) |
| Business rule, validation, orchestration, Model → DTO | [Services/ItemService.cs](../src/Service.Api/Services/ItemService.cs) |
| PostgreSQL query/write | [Infrastructure/Database/Repositories/Item/](../src/Service.Api/Infrastructure/Database/Repositories/Item) |
| Persistence entity | [Models/ItemModel.cs](../src/Service.Api/Models/ItemModel.cs) |
| EF mapping and schema rule | [Infrastructure/Database/Configurations/ItemConfiguration.cs](../src/Service.Api/Infrastructure/Database/Configurations/ItemConfiguration.cs) |
| Resource cache key and TTL | [Infrastructure/Cache/Item/ItemCache.cs](../src/Service.Api/Infrastructure/Cache/Item/ItemCache.cs) |
| Generic Redis serialization, provider and fallback policy | [Infrastructure/Cache/RedisCache.cs](../src/Service.Api/Infrastructure/Cache/RedisCache.cs) |
| Domain error | [Exceptions/Item/ItemNotFoundException.cs](../src/Service.Api/Exceptions/Item/ItemNotFoundException.cs) |
| DI and framework setup | [Extensions/](../src/Service.Api/Extensions) and [Program.cs](../src/Service.Api/Program.cs); see registrations below |
| Database schema evolution | [Infrastructure/Database/Migrations/](../src/Service.Api/Infrastructure/Database/Migrations) |
| Behavior verification | `tests/Service.Api.UnitTests/` or `tests/Service.Api.IntegrationTests/`; use the [placement matrix](TESTING.md#test-placement) |

## Architecture invariants

These are current template conventions to check during review:

- Controllers bind HTTP, call services, invoke DTO → Response mappers and construct HTTP results. They do not access EF or Redis.
- Services validate and orchestrate repositories/caches, and map Model → DTO. Persistence Models are not public HTTP contracts.
- Repositories own EF/PostgreSQL access. Domain caches own resource keys and TTL selection.
- RedisCache owns generic serialization, provider access and fallback behavior. Mappers own DTO → Response conversion.
- Program shows high-level composition and the HTTP pipeline; extensions hold detailed registrations/configuration.

Do not access DbContext from controllers/services, access the Redis provider from
services, return Models as HTTP responses, duplicate response mapping in controllers,
or put domain cache keys in services. Do not add generic repositories/UnitOfWork,
custom middleware or logger wrappers without a demonstrated need. Do not auto-run
migrations at startup, mix LOCAL/DEV/TEST resources, or use destructive Docker/DB
cleanup casually.

For senior review, [current design decisions](ARCHITECTURE.md#current-design-decisions)
explain repository boundaries, domain cache wrappers, DTO/Response separation and
why there is no generic repository/UoW. [Cache consistency](ARCHITECTURE.md#cache-behavior-and-consistency)
covers cache-aside, Redis degradation, stale Action protection, post-commit
invalidation and `CancellationToken.None` after committed writes. Migration
ownership is [explicit](#creating-and-reviewing-migrations); the
[environment comparison](../README.md#local--dev--test) identifies each topology.

## Add a domain: Widget

**Hypothetical recipe only: Widget is not implemented.** Paths/names containing
Widget below are proposed additions, not existing files. Use Item as the reference
for an independent resource; use Action for a resource with a parent relationship.
Decide the intended routes, fields, validation, persistence and cache behavior first.

1. **Model:** add `Models/WidgetModel.cs`, following [ItemModel](../src/Service.Api/Models/ItemModel.cs). Define persisted state, identity and meaningful defaults. Implement `ITimestampedEntity` if adopting the existing timestamp convention.
2. **Enum, if needed:** add `Enums/WidgetStatus.cs` only for actual domain states; inspect [ItemStatus](../src/Service.Api/Enums/ItemStatus.cs). Review persisted numeric values and public string names separately.
3. **Requests:** add create/update input under `Dtos/Widget/Requests/`, following [CreateItemRequest](../src/Service.Api/Dtos/Item/Requests/CreateItemRequest.cs) and [UpdateItemRequest](../src/Service.Api/Dtos/Item/Requests/UpdateItemRequest.cs). Put input validation here so HTTP binding and direct service validation agree.
4. **Service/cache DTO:** add `Dtos/Widget/WidgetDto.cs`, following [ItemDto](../src/Service.Api/Dtos/Item/ItemDto.cs). Decide required JSON members intentionally because this shape is cached.
5. **Response:** add `Dtos/Widget/Responses/WidgetResponse.cs`, following [ItemResponse](../src/Service.Api/Dtos/Item/Responses/ItemResponse.cs). Expose only the intended public fields.
6. **Mapper:** add `Mappers/WidgetMapper.cs`, following [ItemMapper](../src/Service.Api/Mappers/ItemMapper.cs), to convert DTOs to public responses once.
7. **EF configuration:** add `Infrastructure/Database/Configurations/WidgetConfiguration.cs`, following [ItemConfiguration](../src/Service.Api/Infrastructure/Database/Configurations/ItemConfiguration.cs). Define table, keys, lengths, constraints and timestamp mapping; use [ActionConfiguration](../src/Service.Api/Infrastructure/Database/Configurations/ActionConfiguration.cs) when reviewing FK/index/delete behavior.
8. **DbSet:** add `DbSet<WidgetModel> Widgets` to [ServiceDbContext](../src/Service.Api/Infrastructure/Database/ServiceDbContext.cs) for this persisted resource. EF configurations are discovered by `ApplyConfigurationsFromAssembly`; no manual configuration registration is needed. A non-persisted concept would not need a DbSet or table.
9. **Repository interface:** add `Infrastructure/Database/Repositories/Widget/IWidgetRepository.cs`, based on [IItemRepository](../src/Service.Api/Infrastructure/Database/Repositories/Item/IItemRepository.cs). Expose the operations the service needs.
10. **Repository implementation:** add `WidgetRepository.cs` alongside it, following [ItemRepository](../src/Service.Api/Infrastructure/Database/Repositories/Item/ItemRepository.cs). Keep EF queries, tracking, save ownership and provider-specific error handling here; inspect [ActionRepository](../src/Service.Api/Infrastructure/Database/Repositories/Action/ActionRepository.cs) for parent-deletion races rather than catching every database failure as not-found.
11. **Domain cache interface:** add `Infrastructure/Cache/Widget/IWidgetCache.cs`, following [IItemCache](../src/Service.Api/Infrastructure/Cache/Item/IItemCache.cs), for resource-specific get/set/remove operations.
12. **Domain cache implementation:** add `WidgetCache.cs`, following [ItemCache](../src/Service.Api/Infrastructure/Cache/Item/ItemCache.cs). Use the shared ICache, configured prefix and TTL; choose an isolated `widgets` key segment. Do not duplicate the Redis provider or failure policy.
13. **Domain exceptions:** add `Exceptions/Widget/WidgetNotFoundException.cs` derived from [NotFoundException](../src/Service.Api/Exceptions/NotFoundException.cs), following ItemNotFoundException. Existing centralized handling then supplies 404; reuse RequestValidationException for validation. Introduce a new status mapping only for an intentional new error contract.
14. **Service interface:** add `Services/IWidgetService.cs`, following [IItemService](../src/Service.Api/Services/IItemService.cs), with DTO results and cancellation parameters.
15. **Service implementation:** add `Services/WidgetService.cs`, following [ItemService](../src/Service.Api/Services/ItemService.cs): validate, orchestrate repository/cache, map Model → DTO and await writes before invalidation. Use [ActionService](../src/Service.Api/Services/ActionService.cs) for parent/existence safeguards if applicable. Preserve bounded best-effort invalidation after committed writes.
16. **Controller:** add `Controllers/WidgetsController.cs`, following ItemsController: routes, service delegation, mapper calls, response/status metadata and Location for creation. Keep business and persistence logic out.
17. **DI registrations:** add scoped `IWidgetRepository, WidgetRepository` in **DatabaseExtensions**, singleton `IWidgetCache, WidgetCache` in **CacheExtensions**, and explicit scoped `IWidgetService, WidgetService` in **Program.cs**. Add the required namespace imports. Singleton caches must not depend on scoped repositories/DbContext. Keep EF setup in DatabaseExtensions and API/framework setup in ApiExtensions.
18. **Public enum JSON:** if Widget adds a public enum, register its typed `JsonStringEnumConverter<WidgetStatus>` in [ApiExtensions](../src/Service.Api/Extensions/ApiExtensions.cs), following camelCase with `allowIntegerValues: false`. Verify JSON and OpenAPI; do not assume CLR enum names alone define the HTTP representation.
19. **Readiness:** review [PostgresHealthCheck](../src/Service.Api/Infrastructure/Database/PostgresHealthCheck.cs). It currently checks pending migrations and probes Items/Actions; a new DbSet is not automatically a new table probe. Decide whether Widget availability requires another probe and corresponding health tests. No new health endpoint is inherently required.
20. **Migration:** a persisted Widget adds a table. Follow the [migration decision and review guide](#do-i-need-a-migration); do not create a migration before the intended model/mapping changes exist.
21. **Unit tests:** add meaningful defaults, request/enum validation, DTO serialization, mapper, service orchestration, controller delegation and domain-cache policy cases in the matching unit folders. Use [placement](TESTING.md#test-placement) and deterministic doubles.
22. **Integration tests:** verify real repository/schema/migration behavior, HTTP routing/binding/JSON/status/headers, cache behavior and relevant readiness/configuration. Review exact expectations in [SchemaContractTests](../tests/Service.Api.IntegrationTests/Infrastructure/Database/SchemaContractTests.cs) and [EndpointSurfaceTests](../tests/Service.Api.IntegrationTests/Controllers/EndpointSurfaceTests.cs); intentionally extend them instead of weakening assertions.
23. **OpenAPI:** inspect the LOCAL Swagger document/UI for Widget routes, request/response schemas and enums; extend [OpenApiTests](../tests/Service.Api.IntegrationTests/OpenApi/OpenApiTests.cs) for the intended surface.
24. **Documentation:** update the domain/HTTP contract descriptions and README/operation instructions wherever the public contract or workflow changed. Avoid unrelated documentation rewrites.

## Do I need a migration?

| Change | Migration? |
| --- | --- |
| New persisted column or table | YES |
| Relationship, FK, index or constraint change | YES |
| Persistence mapping that changes the schema | YES |
| Service orchestration or logging only | NO |
| Mapper/response formatting only | NO, unless persistence also changes |
| Cache policy only | NO |
| HTTP-only request/response validation or shape | Usually NO; YES if the intended rule also changes persisted schema/constraints |

Compare the actual EF model and schema implications. Names alone do not decide:
a Request length change may also require a database length change. Verify generated
differences rather than assuming a migration is harmless or complete.

## Creating and reviewing migrations

Migrations are intentional, reviewed source changes. **API startup does not migrate.**
This is optional host/IDE tooling: install the pinned SDK and restore packages first.
After an intentional model/configuration change, the authoring command is:

```bash
dotnet tool restore
dotnet ef migrations add AddWidget --project src/Service.Api --output-dir Infrastructure/Database/Migrations
```

`AddWidget` is an example name; choose a descriptive name for the actual change.
Do not run it just to follow this guide without making an intended schema change.

1. Inspect generated `Up`, `Down`, designer metadata and the `ServiceDbContextModelSnapshot` diff. Never blindly commit generated output. Check column types/nullability/defaults, data loss or conversion needs, FK targets, indexes, constraints and delete behavior.
2. Confirm the diff contains only intended schema changes. An unrelated drop/rename or broad snapshot change needs investigation before application. Review rollback data-loss implications even when Down compiles.
3. Apply to LOCAL with `make migrate ENV=local`. Verify the selected configuration first and preserve any data you need; a destructive LOCAL reset is not required for ordinary migration work.
4. Update intentional schema expectations in [SchemaContractTests](../tests/Service.Api.IntegrationTests/Infrastructure/Database/SchemaContractTests.cs) and relevant persistence/health tests. Run the [full automated suite](TESTING.md#commands); generated TEST databases provide clean application without deleting LOCAL data.
5. Review [MigrationTests](../tests/Service.Api.IntegrationTests/Infrastructure/Database/MigrationTests.cs): retain evidence for clean migration application, rollback/reapplication, already-current state and no pending model changes. The current tests assert a single InitialCreate migration and the exact Items/Actions table set; update those expectations intentionally when adding a migration/table. Extend cases when a new migration introduces an upgrade/data transformation that current tests do not exercise.
6. Check that the model and snapshot agree with the command below, then review the entire diff again. This check does not prove safe data migration or replace real PostgreSQL tests.

```bash
dotnet ef migrations has-pending-model-changes --project src/Service.Api
```

## Contract change checklist

| Changed surface | Review before merging |
| --- | --- |
| Request DTO | Required/optional fields, limits and invalid/null input; direct service validation plus HTTP binding; JSON and OpenAPI request schema |
| Response DTO or mapper | Field names/types/nullability, enum formatting and mapper completeness; HTTP JSON and OpenAPI response schema |
| Enum | Accepted/rejected strings and integers, domain validation, persisted values/check constraints, cached representation and OpenAPI |
| Model | Identity/defaults/timestamps, EF mapping, schema/migration impact and service DTO mapping; do not expose the Model over HTTP |
| Route | Verb/path/parameters, nested-resource meaning, status/Location headers, endpoint surface tests and OpenAPI |
| Repository behavior | Tracking/save ownership, ordering, cancellation, provider exceptions and concurrent deletion; real PostgreSQL tests and service assumptions |
| Cache payload | Required members/types and serialization compatibility; old/new readers, malformed payload fallback and real Redis roundtrip tests |
| Cache key | Prefix/resource/id isolation, read/write/invalidation agreement and transition behavior for existing keys |
| Database schema | Up/Down/snapshot, data preservation, constraints/indexes/delete behavior, clean and current migration states, schema and readiness tests |

Adding a required cached DTO member can make old Redis payloads unusable: the
current deserialization failure policy turns that read into a cache miss, then the
service reloads PostgreSQL and can repopulate the cache. Treat the extra database
load and compatibility window intentionally; changing a key does not remove old
keys immediately. Review TTL/expiry and invalidation instead of flushing Redis.
Choose tests for the changed behavior using the [source-first rule](TESTING.md#source-first-test-design).

## Beginner contribution path

1. Run [LOCAL Quick Start](../README.md#quick-start).
2. Use Swagger to create an Item and read its returned id.
3. Trace [POST /items](ARCHITECTURE.md#post-items-walkthrough).
4. Trace [cached GET](ARCHITECTURE.md#get-itemsid-walkthrough).
5. Run the unit project for fast feedback.
6. Run `./scripts/test.sh` for the full suite with isolated dependencies.
7. Read the [test placement matrix](TESTING.md#test-placement).
8. Complete the exercise below, or inspect its existing coverage if already present.
9. Make a small, intentional validation/HTTP change; identify the Request and any matching service/HTTP tests, and apply the contract/migration decision checklists.
10. Attempt schema changes or the Widget recipe after you can explain and verify that smaller change.

### First exercise: two-host configuration isolation

**Search before implementing** so you do not duplicate a regression already covered:

```bash
rg -n 'ApiFactory|KeyPrefix|[Ii]solat|[Oo]verride' tests/Service.Api.IntegrationTests/Startup tests/Service.Api.IntegrationTests/Fixtures
```

Read [ConfigurationTests](../tests/Service.Api.IntegrationTests/Startup/ConfigurationTests.cs)
and [ApiFactory](../tests/Service.Api.IntegrationTests/Fixtures/ApiFactory.cs). If the
complete case is absent, add a hosted regression alongside the startup/configuration
tests: start two ordinary ApiFactory hosts and resolve their CacheOptions; prove
their generated prefixes are distinct. Use an explicit per-host
`WithWebHostBuilder` configuration override for one host, then prove its chosen
prefix takes effect while the independently started host retains its own value.
A derived factory builds another host; do not assume an already-built host changes
in place. Keep both relevant hosts alive during comparison, dispose all factories,
and never mutate process-global environment variables.

Use actual configuration/options resolution, not a mocked options object. This
exercise teaches WebApplicationFactory, configuration precedence, host isolation,
integration placement and existing naming conventions. It requires no production
or schema change and no PostgreSQL/Redis connection because it inspects host
options, not CRUD/readiness. Run the new test by fully qualified name, then the
[hosted tests without infrastructure](TESTING.md#commands). If equivalent coverage
already exists, trace its assertions and report what each proves instead of adding
a duplicate.

## Contributor checklist

Before coding:

- Identify the owning layer using [where changes go](#where-changes-go).
- Identify HTTP/JSON/OpenAPI contract impact, persisted schema impact and cache compatibility/invalidation impact.
- Read the production path and existing tests; choose missing cases and the correct test level.

After coding:

- Run affected unit tests, then the unit project; run relevant integration tests with dedicated TEST prerequisites and the full suite before review.
- If HTTP changed, inspect Swagger/OpenAPI and verify routing, binding, JSON, statuses and headers.
- If persistence changed, review generated migration/schema diffs and verify clean/current states and pending model changes.
- If infrastructure/image behavior changed, run image smoke; if workflow/environment behavior changed, run workflow certification. Use certification before template-level certification, not for every inner-loop edit.
- Review documentation affected by public changes. Run `git diff --check`, inspect `git diff` and `git status --short`; include new files in review, exclude credentials/artifacts and describe the behavior changed plus validation evidence.

For command selection, prerequisites and failure diagnosis, use [TESTING](TESTING.md#commands)
and [troubleshooting](#troubleshooting).
