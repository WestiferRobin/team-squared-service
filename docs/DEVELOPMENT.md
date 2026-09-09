# Development and operation

[Start here: README](../README.md) · [Architecture and request walkthroughs](ARCHITECTURE.md) · [Testing](TESTING.md)

For new and experienced contributors: use this guide to run, inspect, stop and safely change the service. Start with
[LOCAL](#local); use [DEV](#dev) for a containerized background service. See
[configuration](#configuration-sources), [debugging](#debugging) or
[troubleshooting](#troubleshooting) when behavior differs from expectations.
For contributions, jump to [where changes go](#where-changes-go),
[add Widget](#add-a-domain-widget), [migrations](#do-i-need-a-migration),
[contract review](#contract-change-checklist), [first contribution](#beginner-contribution-path),
or the [verification checklist](#contributor-checklist). Test design and helper selection
are in the [testing guide](TESTING.md).

## Shell and working directory

All commands run from the repository root, even though this file lives in `docs/`.
Examples use Bash-compatible syntax on macOS/Linux; `export`, `set -a`, `. file`
and the shell scripts are not native PowerShell commands. The workflow runner also
uses POSIX process groups/signals. Do not assume an unverified Windows shell setup
is interchangeable with this workflow.

Use SDK 8.0.303 as pinned in [global.json](../global.json); roll-forward is disabled.
Docker Engine/Desktop must be running with Compose v2. Git is needed to clone;
Bash runs the shell scripts; Python 3 runs smoke/workflow validation. The optional
HTTP command examples use host `curl`; Swagger can be used instead.

## LOCAL

```text
Host API: Development, 127.0.0.1:5080
    → PostgreSQL: 127.0.0.1:55432, database service_local
    → Redis: 127.0.0.1:56379, cache prefix service-local
```

[compose.local.yml](../docker/compose.local.yml) starts only PostgreSQL and Redis.
The API runs under your IDE, `dotnet run`, or `dotnet watch`. PostgreSQL has a named
persistent volume; Redis storage is disposable.

The canonical first-start commands—configuration, dependency startup, migration
and host API—are in the [README Quick Start](../README.md#quick-start). Reuse that
sequence whenever opening a fresh API terminal; environment exports do not carry
into another terminal automatically.

| Operation | Command / source |
| --- | --- |
| Start dependencies after setup | `docker compose --env-file .env.local -f docker/compose.local.yml up -d --wait --wait-timeout 60` |
| Apply existing migrations, with LOCAL exports loaded | `dotnet ef database update --project src/Service.Api` |
| Run host API | `dotnet run --project src/Service.Api --launch-profile Service.Api` |
| Watch source changes instead | `dotnet watch --project src/Service.Api run` |
| Inspect dependencies | `docker compose --env-file .env.local -f docker/compose.local.yml ps` |
| Follow dependency logs | `docker compose --env-file .env.local -f docker/compose.local.yml logs -f postgres redis` |

The [launch profile](../src/Service.Api/Properties/launchSettings.json) selects
Development and port 5080. For IDE debugging, select `Service.Api` and supply the
same LOCAL connections and cache prefix through the IDE's external environment,
or launch the IDE from the configured shell. Launch settings contain no secrets.
The host API logs appear in its terminal or IDE output pane.

Stop the host API with Ctrl-C before stopping dependencies:

```bash
docker compose --env-file .env.local -f docker/compose.local.yml down
```

Normal `down` removes containers/network but preserves the PostgreSQL volume.
Recreating the same project brings existing data back. This is intentional.

> **Destructive LOCAL reset:** the following removes this project's PostgreSQL
> volume and all its data. Use it only when you intentionally want an empty database.
> Reapply existing migrations after recreating dependencies. It is not a normal repair step.

```bash
docker compose --env-file .env.local -f docker/compose.local.yml down -v
```

## DEV

Topology: `127.0.0.1:18080` → `service-api:8080` → `postgres:5432` / `redis:6379`.
Compose project `service-dev` uses Staging, database `service_dev`, and cache prefix
`service-dev`. PostgreSQL persists in a project-owned named volume; Redis is
recreated without persistence. Host dependency ports `25432` / `26379` are bound
only to loopback and support explicit host-side EF migrations.

```bash
[ -f .env.dev ] || cp .env.example .env.dev
docker compose --env-file .env.dev -f docker/compose.dev.yml config --quiet
docker compose --env-file .env.dev -f docker/compose.dev.yml up -d --wait --wait-timeout 60 postgres redis

# In a fresh shell, configure the host-side migration connection.
set -a
. ./.env.dev
set +a
unset DOTNET_ENVIRONMENT
export ASPNETCORE_ENVIRONMENT=Staging
export ConnectionStrings__Postgres="Host=127.0.0.1;Port=${POSTGRES_PORT:-25432};Database=${POSTGRES_DB:-service_dev};Username=${POSTGRES_USER:-service};Password=${POSTGRES_PASSWORD}"
dotnet ef database update --project src/Service.Api

docker compose --env-file .env.dev -f docker/compose.dev.yml up -d --build service-api
curl --fail http://127.0.0.1:18080/health
curl --fail http://127.0.0.1:18080/ready
docker compose --env-file .env.dev -f docker/compose.dev.yml logs -f service-api
```

Dependency health checks establish server availability; they do not apply EF
migrations. API startup never migrates or seeds. Readiness must report `Healthy`
for full validation; HTTP 200 with `Degraded` means Redis is unavailable. Allow a
short startup period before checking the HTTP endpoints.

Swagger UI: <http://127.0.0.1:18080/swagger>.
JSON: <http://127.0.0.1:18080/swagger/v1/swagger.json>.
Set `API_PORT`, `POSTGRES_PORT`, or `REDIS_PORT` in `.env.dev` to change host ports,
and use those ports in URLs/migration connections. Container connections always
use Compose DNS, not localhost.

```bash
# Rebuild/recreate just the API, retaining running dependencies and their data.
docker compose --env-file .env.dev -f docker/compose.dev.yml up -d --build --no-deps service-api
# Stop while preserving PostgreSQL.
docker compose --env-file .env.dev -f docker/compose.dev.yml down
```

DEV can run background dependencies while another service is debugged locally.
Copies of the service should choose unique project names (`-p`) and host ports.
Each project has its own network and volumes, without a shared global network.

> **Destructive DEV reset:** only run this when you intend to delete this DEV
> project's persisted database. Stop ordinary work first; existing rows will be lost.

```bash
docker compose --env-file .env.dev -f docker/compose.dev.yml down -v
```

DEV is the mock-production/background topology: debug Service A locally while
Services B/C run as DEV containers. It uses the actual Compose API service, not a
host API connected to DEV dependencies. Ctrl-C on `logs -f` stops log following;
containers continue running until stopped with Compose.

## TEST overview

Unit tests require no running infrastructure:

```bash
dotnet test tests/Service.Api.UnitTests
# Optional: all tests that do not require PostgreSQL or Redis.
dotnet test Service.sln --filter "Category!=Postgres&Category!=Redis"
```

Run the full solution and certify the actual Docker image separately:

```bash
[ -f .env.test ] || cp .env.example .env.test
./scripts/test.sh
./scripts/smoke.sh
```

Both scripts accept only `POSTGRES_USER` and `POSTGRES_PASSWORD` from `.env.test`,
using disposable template defaults when absent. Values must be unquoted letters,
digits, underscores, dots or dashes. Other dotenv settings and inherited connection
strings cannot redirect tests: scripts force database `service_test`, host loopback
connections, cache prefix `service-test`, unique project names, and ephemeral host
ports assigned by Docker. Scripts do not execute the dotenv file as shell code.

`test.sh` restores tools/packages, provisions only PostgreSQL and Redis from
`compose.test.yml`, waits for health, and runs the full solution. Integration tests
use WebApplicationFactory with real dependencies. Each PostgreSQL fixture creates,
migrates and deletes its own unique database; Redis tests use unique keys/prefixes
and never flush shared state. Ordinary test hosts explicitly disable OpenAPI.

`smoke.sh` provisions a different disposable TEST project, explicitly applies EF
migrations, builds the root Dockerfile, and runs the image with Staging, container
DNS connections and OpenAPI enabled. Host-side Python polls for `200 Healthy` with
a 60-second deadline, checks liveness/readiness and Swagger JSON/UI, and exercises
Item/Action creation, canonical reads, updates, lists, cache reads and cascade
not-found behavior. No curl or SDK is required inside the API image.

Both scripts preserve failure status, collect bounded logs on failure, and clean
up their own containers, network and volumes on exit, Ctrl-C or termination. Smoke
also removes its unique image tag. They cannot remove LOCAL/DEV projects or data.
An uncatchable kill or Docker daemon failure can prevent cleanup; the reported
unique project name identifies resources for manual recovery. No fixed global
container names are used. Existing test stacks are not reused or removed.

`compose.test.yml` remains dependency-only with temporary PostgreSQL/Redis storage.
For manual inspection its defaults are project `service-test`, database
`service_test`, ports `15432` / `16379`; automated scripts override project/ports.

```bash
docker compose --env-file .env.test -f docker/compose.test.yml config --quiet
```

Unit tests exercise isolated behavior; hosted integration tests exercise ASP.NET
Core and real PostgreSQL/Redis boundaries where needed. Running the whole solution
directly with `dotnet test` does not provision infrastructure. Prefer `test.sh` for
that job; it supplies dedicated TEST connections and performs cleanup.

Ordinary integration hosts deliberately isolate application configuration and
disable Swagger. Dedicated configuration hosts load application JSON defaults with
intentional overrides. Do not expect a developer shell setting to reconfigure an
ordinary test host. See [test placement](TESTING.md#test-placement) and
[helper selection](TESTING.md#test-helpers) before writing a test.

## Workflow certification

`test.sh` and `smoke.sh` remain the normal entry points. For the broader LOCAL /
DEV persistence and script-failure checks, run this opt-in certification separately:

```sh
python3 scripts/certify-workflows.py
```

It uses the **actual** `compose.local.yml` and `compose.dev.yml` with unique
`service-cert-local-*` / `service-cert-dev-*` project names and their documented
ports. LOCAL runs the host API with the `Service.Api` Development launch profile;
DEV runs the Compose API in Staging. Certification refuses occupied ports rather
than stopping existing listeners. Stop your own conflicting workflow first, or
run certification on an otherwise available development machine.

No developer dotenv file is loaded for LOCAL/DEV certification. The runner supplies
disposable credentials, isolates child-process configuration, and creates its own
persistent PostgreSQL volumes. It checks migrations, Swagger, health, Item/Action
CRUD, real cache population/invalidation, and cascade behavior. Each workflow then
performs two `down` / recreation cycles **without `-v`**, proving migration and row
persistence. Final targeted row cleanup is followed by `down -v` against only the
certification projects, also verifying destructive reset removes those owned
volumes. Existing LOCAL/DEV volumes are never reset.

While both certification workflows remain running, the runner executes normal
TEST/smoke validation, two controlled failures per script, and one SIGTERM case per
script. It verifies exit codes, emitted failure logs, resource removal, and the
continued presence of the LOCAL/DEV records. Docker inventories and existing
container start times are compared to detect leaks or interference. Avoid concurrent
Docker changes while this explicit certification is running.

`SERVICE_WORKFLOW_CERTIFICATION` is a **certification-only**, default-off hook in
both scripts. `fail` returns status 73; `term` delivers SIGTERM to the script at a
deterministic provisioned checkpoint and must return 143. The test checkpoint is
immediately before the test command; the smoke checkpoint follows successful HTTP
smoke validation, while its API/image/dependencies still exist. This proves the
SIGTERM trap and cleanup at that checkpoint; it does not claim exhaustive signal
coverage during every external command or prove cleanup after an uncatchable kill.
Leave this variable unset for normal use.

Command output is retained under `artifacts/workflows-<run-id>/`. No production
source, schema, package, or HTTP contract changes are part of certification.

## Configuration sources

| Source | Role |
| --- | --- |
| [appsettings.json](../src/Service.Api/appsettings.json) | Safe base defaults; empty infrastructure connections; Swagger disabled |
| [appsettings.Development.json](../src/Service.Api/appsettings.Development.json) | Development logging overrides and Swagger enabled |
| [.env.example](../.env.example) | Disposable credential example copied into separate workflow files |
| Environment variables | Supply application connections, cache prefix and optional overrides |
| [launchSettings.json](../src/Service.Api/Properties/launchSettings.json) | Local launch environment and listener; not container configuration |
| Test factories | Build deliberately isolated hosts and explicit test overrides |

For normal application settings, environment-specific JSON overrides base JSON;
matching environment variables override JSON; command-line configuration can
explicitly override application settings. Use `__` for nesting, such as
`Cache__KeyPrefix` for `Cache:KeyPrefix`. The launch profile supplies settings to the
host process; the documented commands unset conflicting environment selectors.
Use a fresh shell for each workflow instead of mixing LOCAL and DEV exports.

**Compose interpolation is separate.** `--env-file .env.local` supplies values for
`${...}` substitutions in Compose. Already-exported shell variables can override
those file values during interpolation. This does not automatically pass every
variable into the API container; DEV explicitly lists its container environment.

.NET does not load arbitrary dotenv files. The LOCAL examples explicitly source a
trusted file and export application connection strings. TEST scripts parse only
their documented credential keys. The certification runner supplies its own
LOCAL/DEV credentials and child-process configuration.

> **Credentials:** .env.example values are disposable examples, not production
> secrets. Real developer `.env*` files are ignored by Git and Docker. Never commit
> real credentials or put them in launch settings. Source only trusted shell-compatible files.

| Key/environment variable | Purpose |
| --- | --- |
| `ConnectionStrings__Postgres` | Required PostgreSQL connection; empty appsettings default |
| `ConnectionStrings__Redis` | Optional Redis connection; empty default |
| `Cache__DefaultTtlSeconds` | Default 300; valid 1–86400 |
| `Cache__KeyPrefix` | Default `service`; nonblank |
| `Logging__LogLevel__...` | Built-in logging configuration |
| `AllowedHosts` | Default `*`; set deployment hostnames when deploying |
| `OpenApi__Enabled` | Gate Swagger JSON and UI together; base false |
| `ASPNETCORE_ENVIRONMENT` | Select environment settings; do not also set conflicting DOTNET_ENVIRONMENT |
| `ASPNETCORE_HTTP_PORTS` | Container defaults to 8080 |
| `ASPNETCORE_URLS` / `--urls` | Explicit listener override |
| `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` | Compose database configuration |
| `POSTGRES_PORT`, `REDIS_PORT`, `API_PORT` | Compose loopback port bindings (API_PORT applies to DEV) |

Appsettings contains no credentials. Invalid cache options fail startup. No extra
services, provider configuration or background processing are required.

## Applying existing migrations

A migration records schema changes so a database can be brought to the version
expected by this source. Starting PostgreSQL creates a database, not the API's
Items/Actions schema. API startup never applies migrations or seeds data.

With the correct LOCAL or DEV host connection exported, run:

```bash
dotnet tool restore
dotnet ef database update --project src/Service.Api
```

The local EF tool version is pinned in [.config/dotnet-tools.json](../.config/dotnet-tools.json).
A successful command applies pending migrations or reports the database is already
current. `/ready` then verifies connectivity, migration state and access to Items
and Actions. Redis failure can still make readiness Degraded after a successful
migration. Check its response body, not only the HTTP status.

Use the DEV host-mapped PostgreSQL port for host-side EF commands; the DEV API
itself connects through `postgres:5432`. For intentional schema changes, follow
[creating and reviewing migrations](#creating-and-reviewing-migrations).

## Swagger and health

OpenAPI JSON describes the API; Swagger UI is the interactive browser that reads
that document and lets you send requests. This service uses Swashbuckle to generate
its `v1` document through AddApi. Health endpoints are outside the Swagger document.
Swagger describes success contracts; the complete error policy is in the
[HTTP contract](ARCHITECTURE.md#http-contract).

| Workflow | UI | JSON | Gate |
| --- | --- | --- | --- |
| LOCAL | http://127.0.0.1:5080/swagger | http://127.0.0.1:5080/swagger/v1/swagger.json | Development JSON enables OpenApi:Enabled |
| DEV | http://127.0.0.1:18080/swagger | http://127.0.0.1:18080/swagger/v1/swagger.json | Compose explicitly enables it in Staging |
| TEST | Test-controlled address | Test-controlled address | Ordinary hosts disable it; dedicated tests/smoke enable it explicitly |

Use changed host ports in URLs if you override defaults. `/swagger/index.html` is
the UI page. Base settings disable Swagger, including ordinary Production settings.
Program gates both UI and JSON on the same `OpenApi:Enabled` setting.

| Dependency state | /health | /ready |
| --- | --- | --- |
| PostgreSQL/schema ready; Redis available | 200 Healthy | 200 Healthy |
| PostgreSQL/schema ready; Redis unavailable/unconfigured | 200 Healthy | 200 Degraded |
| PostgreSQL unavailable/unconfigured/unmigrated | 200 Healthy | 503 Unhealthy |

Liveness checks that the API can answer; it does not query dependencies. Readiness
checks database connectivity/schema and optional Redis access. Degraded Redis does
not necessarily prevent CRUD: the service can use PostgreSQL with slower reads.

```bash
curl --fail http://127.0.0.1:5080/health
curl --fail http://127.0.0.1:5080/ready
```

Expect `Healthy` from both with LOCAL fully available. For DEV, use port 18080.
A Compose dependency marked healthy means its server is available; migrations and
application readiness are separate checks.

## Debugging

1. Start LOCAL and select the Service.Api launch profile in the IDE. Supply the
   same environment as the Quick Start API terminal.
2. Set a breakpoint in ItemsController.Create or Get, then send the request in Swagger.
3. Follow the [POST](ARCHITECTURE.md#post-items-walkthrough) or
   [cached GET](ARCHITECTURE.md#get-itemsid-walkthrough) breakpoint sequence.
4. For database issues, inspect `compose ... ps`, the selected connection and migration
   result before changing code. For cache issues, inspect Redis logs and the readiness
   body; a cache miss can legitimately reach the repository.
5. For integration failures requiring PostgreSQL/Redis, run `./scripts/test.sh` in a
   separate repository-root terminal. It provisions dedicated resources; do not point
   fixture tests at LOCAL/DEV databases.

Services log cache misses/not-found context at Debug, which is normally hidden.
For a LOCAL debugging session, stop the current host API, retain the LOCAL
connection exports in its terminal, and launch with a temporary category override:

```bash
env 'Logging__LogLevel__Service.Api.Services=Debug' dotnet run --project src/Service.Api --launch-profile Service.Api
```

`env` passes the dotted category name to this process without requiring it to be a
Bash variable name. The override ends with that process. [Logging ownership](ARCHITECTURE.md#logging-and-exceptions)
explains which component owns each event.

## Troubleshooting

| Symptom | Check | Action |
| --- | --- | --- |
| Wrong/missing SDK | `dotnet --list-sdks` and global.json | Install 8.0.303; the pin disables roll-forward. Reopen the terminal, then run `dotnet --version` from the repo |
| PostgreSQL connection failure | `docker compose --env-file .env.local -f docker/compose.local.yml ps`; connection host/port/database | Start the correct dependencies; reload that workflow's exports. For DEV use its file and host migration port |
| Redis unavailable / /ready Degraded | Redis container health/logs and ConnectionStrings:Redis | Restore the intended Redis endpoint; PostgreSQL fallback can still serve CRUD |
| /ready Unhealthy | PostgreSQL logs, connection and migration result | Apply existing migrations to the intended database once it is reachable |
| Swagger 404 | Environment and OpenApi:Enabled | Use the LOCAL launch profile or DEV Compose; remove unintended overrides and restart |
| Port already in use | Existing API/Compose processes and configured ports | Stop your own conflicting process or choose documented host-port overrides; update URLs/connections. Do not stop unrelated services |
| Migration/table missing | `dotnet ef database update --project src/Service.Api` with correct exports | Apply existing migrations; container health alone does not create tables |
| Unexpected old rows | Same Compose project and persistent PostgreSQL volume | Data surviving `down` is expected. Delete only intended records; reset the volume only when all its data is disposable |
| Integration infrastructure unavailable | Whether tests were launched directly without dedicated connections | Run `./scripts/test.sh`; isolated unit tests need no running infrastructure |
| Workflow certification refuses to start | Ports 5080, 55432, 56379, 18080, 25432, 26379; concurrent Docker activity | Make the documented ports available without disrupting unrelated resources; read artifacts/workflows-&lt;run-id&gt; logs |

Do not use `down -v` as routine troubleshooting. If a command fails, fix the reported
setup problem before continuing to migration or application startup.

## Docker layout

```text
Dockerfile
docker/
├── compose.local.yml
├── compose.dev.yml
└── compose.test.yml
scripts/
├── test.sh
├── smoke.sh
└── certify-workflows.py
```

One multi-stage Dockerfile builds with SDK 8.0.303 and runs
`Service.Api.dll` as non-root on port 8080 in the ASP.NET 8 runtime. DEV and image
smoke share it. The runtime image contains no credentials, SDK, curl or Docker HEALTHCHECK.
Compose dependency health checks and host HTTP polling cover validation.


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
After hypothetical Widget model/configuration changes, with the LOCAL setup and
connection from the [Quick Start](../README.md#quick-start), the authoring command is:

```bash
dotnet tool restore
dotnet ef migrations add AddWidget --project src/Service.Api --output-dir Infrastructure/Database/Migrations
```

`AddWidget` is an example name; choose a descriptive name for the actual change.
Do not run it just to follow this guide without making an intended schema change.

1. Inspect generated `Up`, `Down`, designer metadata and the `ServiceDbContextModelSnapshot` diff. Never blindly commit generated output. Check column types/nullability/defaults, data loss or conversion needs, FK targets, indexes, constraints and delete behavior.
2. Confirm the diff contains only intended schema changes. An unrelated drop/rename or broad snapshot change needs investigation before application. Review rollback data-loss implications even when Down compiles.
3. Apply to LOCAL with `dotnet ef database update --project src/Service.Api`. Verify the selected connection first and preserve any data you need; a destructive LOCAL reset is not required for ordinary migration work.
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
