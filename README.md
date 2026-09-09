# Service.Api Template

A reusable ASP.NET Core example service demonstrating Item CRUD, Action CRUD,
a parent/child relationship, PostgreSQL persistence, and optional Redis caching.
The API project is `Service.Api`; Item and Action are example resources.

## Architecture

Use .NET SDK **8.0.303** (pinned in `global.json`), targeting `net8.0`.
Docker Engine/Desktop and Compose v2 are required for infrastructure tests.

```text
Service.sln
├── src/Service.Api
│   ├── Program.cs
│   ├── Extensions
│   │   ├── ApiExtensions.cs
│   │   ├── DatabaseExtensions.cs
│   │   └── CacheExtensions.cs
│   ├── Controllers
│   ├── Models
│   │   ├── ItemModel.cs
│   │   ├── ActionModel.cs
│   │   └── ITimestampedEntity.cs
│   ├── Dtos
│   │   ├── Item
│   │   │   ├── ItemDto.cs
│   │   │   ├── Requests
│   │   │   └── Responses
│   │   └── Action
│   │       ├── ActionDto.cs
│   │       ├── Requests
│   │       └── Responses
│   ├── Services
│   ├── Mappers
│   │   ├── ItemMapper.cs
│   │   └── ActionMapper.cs
│   ├── Infrastructure
│   │   ├── Database
│   │   │   ├── ServiceDbContext.cs
│   │   │   ├── Configurations
│   │   │   ├── Migrations
│   │   │   ├── PostgresHealthCheck.cs
│   │   │   └── Repositories/{Item,Action}
│   │   └── Cache
│   │       ├── ICache.cs / RedisCache.cs
│   │       ├── CacheOptions.cs / RedisHealthCheck.cs
│   │       ├── Item/IItemCache.cs / ItemCache.cs
│   │       └── Action/IActionCache.cs / ActionCache.cs
│   ├── Exceptions
│   └── Enums
├── tests/Service.Api.UnitTests
└── tests/Service.Api.IntegrationTests
```

One production project; both test projects reference it.

`Program.cs` is the composition root: it keeps ItemService and ActionService
registrations, the Redis startup warning, middleware order, and controller/health
endpoint mappings explicit. Registration details live in `Extensions`:

- `AddApi()` configures controllers, JSON, Problem Details, exception handling, and OpenAPI generation.
- `AddDatabase()` configures PostgreSQL/EF, repositories, and the PostgreSQL health check.
- `AddCache()` configures Redis, generic/domain caches, and the Redis health check.

```text
Request → Controller → Service → DTO → Mapper → Response → HTTP
                          ├→ Repository → ServiceDbContext → PostgreSQL
                          ├→ Domain Cache → ICache → RedisCache → Redis
                          └→ ILogger<T> → shared .NET logging infrastructure
```

| Component | Responsibility |
| --- | --- |
| Request | Inbound HTTP fields and validation attributes |
| Controller | Bind input, call service and mapper, return HTTP results |
| Service | Validate requests, coordinate persistence/cache, decide not-found errors, map Model → DTO |
| DTO | Service output and typed cache payload; no navigation properties |
| Mapper | Explicit ItemDto → ItemResponse or ActionDto → ActionResponse conversion |
| Response | Outbound HTTP contract |
| Model | ItemModel or ActionModel persistence entity |
| Repository | EF queries, tracking, ordering and completed writes; returns entities |
| Domain cache | Item/Action key, DTO type and TTL policy |
| ICache / RedisCache | Generic typed JSON, bounded operations and cache failure handling |

ItemService depends on IItemRepository, IItemCache, and ILogger<ItemService>.
ActionService depends on IActionRepository, IActionCache, and ILogger<ActionService>.
Controllers do not access persistence or caches.
Services do not access DbContext, construct cache keys, or return HTTP Responses.

Repository write methods save internally. A save commits **all pending tracked
changes on the scoped ServiceDbContext**; do not stage unrelated changes across
repository calls. Mutation methods consume entities from GetForUpdateAsync on the
same scoped context. Reads are no-tracking; lists order by CreatedAt then Id.
There is no custom UnitOfWork. Action creation maps only the specific parent FK
failure to a false result; the service decides the parent-not-found error.

ServiceDbContext, repositories and services are scoped. ICache, ItemCache and
ActionCache are singleton: their dependencies are singleton-safe and they hold no
request state or DbContext. CacheOptions uses IOptions with startup validation.

Domain names, domain folders and domain namespaces are singular: `Item` and
`Action` (`Dtos/Item`, `Dtos/Action`, `Repositories/Item`, `Repositories/Action`,
`Cache/Item`, `Cache/Action`). Plural names express collection semantics:
`/items`, `/actions`, `DbSet<ItemModel> Items`, `DbSet<ActionModel> Actions`, SQL `Items` and
`Actions`, and the `ItemModel.Actions` navigation. Category folders such as Requests
and Responses retain their existing names.

Services use standard .NET logging directly:

- ItemService → `ILogger<ItemService>` → `Service.Api.Services.ItemService`
- ActionService → `ILogger<ActionService>` → `Service.Api.Services.ActionService`

`ILogger<T>` gives each class a category while all loggers use the same underlying
.NET logging infrastructure: the host-managed `ILoggerFactory` and configured
providers. ASP.NET Core supplies these loggers automatically through DI.
Services use constant Debug templates with Domain and resource IDs for missing
resources, cache fallback, parent FK races, and stale Action rejection. Enable
Debug for either service category, or `Service.Api.Services`, when diagnosing
behavior. No request payloads or routine CRUD success
messages are logged. RedisCache owns cache failure warnings; ApiExceptionHandler
owns sanitized unexpected-error logging. Services do not catch/log/rethrow failures.
Stale-cache logs describe requesting removal, since removal is best-effort.

Services throw `ItemNotFoundException` or `ActionNotFoundException`, derived from
`NotFoundException`. IDs remain internal read-only properties; HTTP details remain
unchanged. Missing parents in Action workflows use ItemNotFoundException.
Typed Create/Update validation overloads reuse DataAnnotations through a private
ValidateAnnotations helper for direct callers outside MVC. RequestValidationException
remains shared, including the empty Action parent-ID check.

## Domain and persistence

```text
Item 1 ─── many Actions
Actions.ItemId → Items.Id (ON DELETE CASCADE)
```

Item: UUID Id, Name, Status, CreatedAt, UpdatedAt, and an Actions navigation.
Action: UUID Id, required ItemId, Name, Type, CreatedAt, UpdatedAt, and Item navigation.
DTOs contain scalar fields only, without navigation graphs or embedded child lists.

Names are required, nonblank and limited to 200 characters. Names need not be
unique. ItemStatus is `active`/`archived` (stored as 0/1). ActionType is
`create`/`update`/`delete` (stored as 0/1/2). JSON number tokens and unknown enum
values are rejected. The .NET 8 string-enum converter also accepts quoted numbers
for defined values (for example, `"0"`); clients should send the named strings.
ActionType is an editable classification; it does not execute an operation or
represent audit history.

`Item` and `Action` are domain names. Classes specify their role: persistence
entities are `Service.Api.Models.ItemModel` and `Service.Api.Models.ActionModel`,
in `Models/ItemModel.cs` and `Models/ActionModel.cs`. Use these types directly,
without model aliases. Domain folders and namespaces remain `Item` and `Action`;
SQL tables remain `Items` and `Actions`. Both models implement
`ITimestampedEntity`, processed in one pass by `ServiceDbContext` for synchronous and
asynchronous saves. Both entities receive UTC timestamps on EF insertion. Updates preserve CreatedAt
and refresh UpdatedAt, including unchanged PUT values. Bulk/direct SQL bypasses
this timestamp handling. Action ownership is immutable through the API and normal
EF saves; PUT does not accept ItemId. PostgreSQL enforces the parent FK and cascades
Action deletion when its Item is deleted. Actions.ItemId is indexed.

## HTTP contract

| Method | Route | Request | Success |
| --- | --- | --- | --- |
| GET | `/items` | None | 200 + ItemResponse array |
| POST | `/items` | `{ "name": "Example" }` | 201 + ItemResponse + Location |
| GET | `/items/{itemId}` | None | 200 + ItemResponse |
| PUT | `/items/{itemId}` | `{ "name": "Updated", "status": "archived" }` | 200 + ItemResponse |
| DELETE | `/items/{itemId}` | None | 204 |
| GET | `/items/{itemId}/actions` | None | 200 + ActionResponse array |
| POST | `/items/{itemId}/actions` | `{ "name": "Example", "type": "create" }` | 201 + ActionResponse + Location |
| GET | `/actions` | None | 200 + ActionResponse array |
| POST | `/actions` | `{ "itemId": "<uuid>", "name": "Example", "type": "create" }` | 201 + ActionResponse + Location |
| GET | `/actions/{actionId}` | None | 200 + ActionResponse |
| PUT | `/actions/{actionId}` | `{ "name": "Updated", "type": "update" }` | 200 + ActionResponse |
| DELETE | `/actions/{actionId}` | None | 204 |

ItemResponse: `id`, `name`, `status`, `createdAt`, `updatedAt`.
ActionResponse: `id`, `itemId`, `name`, `type`, `createdAt`, `updatedAt`.
Lists order by CreatedAt then Id. Item creation defaults to active.
Both Action creation routes return canonical Location `/actions/{actionId}`.
Nested creation takes the ItemId only from the route. The separate nested request
DTO has no ItemId; Action request DTOs reject unmapped fields, including attempts
to supply ItemId to nested creation or update. Top-level Action creation requires
a non-empty ItemId. No nested Action get/update/delete endpoints are exposed.

Missing resources/parents return 404 Problem Details. Existing parents without
Actions return `[]`; nested operations on missing parents return 404. Invalid
input returns 400. Unexpected failures return generic 500 Problem Details without
internal exception information. Unexpected application logs omit raw exception
messages/inner exceptions. The framework's duplicate exception logging is disabled.
There are no production debug endpoints. Resource routes use `/items` and `/actions`.

## Developer workflows: LOCAL, DEV, TEST

| Workflow | API | Dependencies | Environment | OpenAPI |
| --- | --- | --- | --- | --- |
| LOCAL | IDE debugger / `dotnet watch` | Docker PostgreSQL + Redis | Development | Enabled by Development settings |
| DEV | Docker, same root Dockerfile | Docker PostgreSQL + Redis | Staging | Explicitly enabled by Compose |
| TEST | Unit tests; WebApplicationFactory integration hosts; separate image smoke | Isolated disposable Docker projects | Testing (smoke uses Staging) | Disabled in ordinary hosts; enabled explicitly for OpenAPI tests/smoke |

Prerequisites: .NET SDK **8.0.303**, Docker Engine/Desktop with Compose v2,
Bash, and Python 3 for image smoke validation. Run commands from the repository root.
The local EF tool is pinned to 8.0.30. Restore before starting:

```sh
dotnet tool restore
dotnet restore
dotnet build Service.sln
```

`.env.example` contains common disposable credentials; each Compose file supplies
its own database/port defaults. Copy it separately for each workflow. Actual
`.env.local`, `.env.dev`, and `.env.test` files are ignored by Git and Docker.
.NET does not load dotenv files. The shell examples below source only your trusted
local file; keep values simple and shell-compatible, without connection-string
delimiters. Never put credentials or connection strings in launch settings.

## LOCAL: API under the debugger

Topology: host API `127.0.0.1:5080` → host-mapped PostgreSQL `55432` and Redis
`56379`. Compose project `service-local` contains only dependencies. PostgreSQL
uses database `service_local` and a persistent named volume. Redis is disposable.

```sh
# Copy once; do not overwrite an existing file.
[ -f .env.local ] || cp .env.example .env.local
docker compose --env-file .env.local -f docker/compose.local.yml config --quiet
docker compose --env-file .env.local -f docker/compose.local.yml up -d --wait --wait-timeout 60

# Use a fresh shell for each workflow; do not mix LOCAL and DEV overrides.
set -a
. ./.env.local
set +a
unset DOTNET_ENVIRONMENT OpenApi__Enabled
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Postgres="Host=127.0.0.1;Port=${POSTGRES_PORT:-55432};Database=${POSTGRES_DB:-service_local};Username=${POSTGRES_USER:-service};Password=${POSTGRES_PASSWORD}"
export ConnectionStrings__Redis="127.0.0.1:${REDIS_PORT:-56379},connectTimeout=1000,asyncTimeout=1000,connectRetry=0"
export Cache__KeyPrefix=service-local
dotnet ef database update --project src/Service.Api
dotnet watch --project src/Service.Api run
```

The `Service.Api` launch profile selects Development and `http://127.0.0.1:5080`.
For IDE debugging, select that profile and supply the same connection strings and
cache prefix through the IDE's external environment, or launch the IDE from the
configured shell. The profile itself contains no secrets.

Swagger UI: <http://127.0.0.1:5080/swagger>.
JSON: <http://127.0.0.1:5080/swagger/v1/swagger.json>.
Check <http://127.0.0.1:5080/health> and <http://127.0.0.1:5080/ready>.
Stop the API with Ctrl-C, then stop dependencies:

```sh
# Preserves PostgreSQL data.
docker compose --env-file .env.local -f docker/compose.local.yml down
# Explicit destructive reset of this LOCAL project's database volume only.
docker compose --env-file .env.local -f docker/compose.local.yml down -v
```

## DEV: API and dependencies in Docker

Topology: `127.0.0.1:18080` → `service-api:8080` → `postgres:5432` / `redis:6379`.
Compose project `service-dev` uses Staging, database `service_dev`, and cache prefix
`service-dev`. PostgreSQL persists in a project-owned named volume; Redis is
recreated without persistence. Host dependency ports `25432` / `26379` are bound
only to loopback and support explicit host-side EF migrations.

```sh
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

```sh
# Rebuild/recreate just the API, retaining running dependencies and their data.
docker compose --env-file .env.dev -f docker/compose.dev.yml up -d --build --no-deps service-api
# Stop while preserving PostgreSQL.
docker compose --env-file .env.dev -f docker/compose.dev.yml down
# Explicit destructive DEV reset.
docker compose --env-file .env.dev -f docker/compose.dev.yml down -v
```

DEV can run background dependencies while another service is debugged locally.
Copies of the service should choose unique project names (`-p`) and host ports.
Each project keeps its own network and volumes; no global shared network is added.

## TEST: automated isolated validation

Unit tests require no running infrastructure:

```sh
dotnet test tests/Service.Api.UnitTests
# Optional: all tests that do not require PostgreSQL or Redis.
dotnet test Service.sln --filter "Category!=Postgres&Category!=Redis"
```

Run the full solution and certify the actual Docker image separately:

```sh
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

```sh
docker compose --env-file .env.test -f docker/compose.test.yml config --quiet
```

The full suite covers Item/Action HTTP behavior, ownership, validation, timestamping,
PostgreSQL constraints/migrations, real Redis payloads/TTL, cache invalidation and
outage fallback. Focused tests also prove invalid CacheOptions fail actual host
startup, missing/blank Redis startup warnings, sanitized provider warnings, and
caller cancellation after provider work begins (including providers that ignore
cancellation). Signaled pending tasks test each cache operation against its existing
two-second wait limit, with an independent bounded test deadline. Payload tests
mutate one field of a proven valid payload to isolate missing-required-field and
unknown-enum failures. Explicit Development, Staging and Production hosts verify
Swagger exposure over HTTP and assert the selected environment.

It uses no EF InMemory, SQLite or mocked query providers. Missing
infrastructure configuration fails instead of silently skipping tests.

## OpenAPI and migrations

Swashbuckle.AspNetCore **10.2.3** generates one `Service.Api` v1 document through
`AddApi()`. `Program.cs` visibly gates both Swagger middleware components on
`OpenApi:Enabled`. Base settings disable it; Development enables it; DEV Compose
enables it explicitly in Staging. Controller API Explorer supplies metadata without
an extra minimal-API explorer registration.

The document includes all Item/Action routes, request/response DTOs and named string
enums. Five targeted response attributes correct inferred create/delete statuses
to 201/204. Error behavior remains documented in the HTTP contract above; Swagger
is not an exhaustive error catalogue. Health endpoints remain outside Swagger.
The enum schemas describe named strings; the .NET 8 quoted-number caveat above
still applies. Enabling documentation adds routes without changing resource routes.

No migrations are added by this refactor. Normal startup never changes schema.
For a future intentional model change, with the correct environment connection:

```sh
dotnet ef migrations add <MigrationName> --project src/Service.Api --output-dir Infrastructure/Database/Migrations
dotnet ef migrations has-pending-model-changes --project src/Service.Api
```

## Redis and health

Both resources use GET-by-ID cache-aside. Services read their domain cache, load
PostgreSQL on a miss, map to DTO and populate the cache. Create does not populate
cache; lists are uncached. Update/delete invalidate only after repository success.
Post-commit invalidation uses CancellationToken.None; request-driven I/O propagates
cancellation. Cache failures produce safe warnings and permit PostgreSQL fallback.

| Domain cache | Payload | Key | Absolute TTL |
| --- | --- | --- | --- |
| ItemCache | ItemDto | `{prefix}:items:{itemId:D}` | DefaultTtlSeconds |
| ActionCache | ActionDto | `{prefix}:actions:{actionId:D}` | DefaultTtlSeconds |

The default prefix is `service` and TTL is 300 seconds. JSON uses the configured
MVC serializer options. Required payload members make incomplete Item/Action JSON
a cache miss. Generic Redis operations have a two-second wait budget.

**Action cache hits still query PostgreSQL.** ActionService calls ExistsAsync before
returning a cached Action. If the row is absent, it removes the stale entry and
returns 404. This protects reads following a completed parent Item deletion, which
cascades to Actions without coupling ItemService to ActionCache. An existence
check is not atomic with a concurrent deletion; overlapping requests can still race.

ItemResponse has no Actions/count, so Action mutations do not invalidate Item cache.
Redis is not transactional with PostgreSQL: concurrent fills, failed invalidation
or late completion after the wait budget can leave stale values until TTL expiry.
The Action safeguard protects absent rows, not freshness of existing-row updates.

| State | `/health` | `/ready` |
| --- | --- | --- |
| PostgreSQL/schema ready, Redis available | 200 Healthy | 200 Healthy |
| PostgreSQL/schema ready, Redis missing/unavailable | 200 Healthy | 200 Degraded |
| PostgreSQL missing/unavailable/unmigrated | 200 Healthy | 503 Unhealthy |

Liveness runs no dependency checks. Readiness checks PostgreSQL connectivity,
pending migrations and access to Items/Actions, with optional Redis probing.

## Configuration

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

## Docker layout

```text
Dockerfile
docker/
├── compose.local.yml
├── compose.dev.yml
└── compose.test.yml
scripts/
├── test.sh
└── smoke.sh
```

One unchanged multi-stage Dockerfile builds with SDK 8.0.303 and runs
`Service.Api.dll` as non-root on port 8080 in the ASP.NET 8 runtime. DEV and image
smoke share it. No credentials, runtime SDK, curl or Docker HEALTHCHECK are added.
Compose dependency health checks and host HTTP polling cover validation.

## Copying the template

`Service.Api` is a placeholder, not a framework requirement. For example, a copied
service can use `User.Api` and `UserDbContext`, or retain `ServiceDbContext`.

1. Copy source/configuration into the new repository without `.git`, `.env`, build
   outputs or test artifacts.
2. Rename solution/project directories and files; update namespaces, solution
   entries, test ProjectReferences, Docker paths/entrypoint and documented commands.
3. If renaming the context, update DbContextOptions, DI, repositories, health checks,
   fixtures, migration designer attributes and snapshot identifiers together.
4. Choose service-specific database/test names and cache prefixes. Update fixture
   database safety checks consistently; configure credentials outside appsettings.
5. Keep migration IDs and SQL operations for naming-only changes. Run the pending
   model check and test both fresh and existing migrated databases. Use reviewed
   migrations for actual schema changes.
6. Replace the example resources as the real service develops, then run build,
   PostgreSQL/Redis tests and Docker smoke checks.

A new timestamped model implements ITimestampedEntity; the existing save-time pass
handles its timestamps without another model-specific loop. Keep persistence in
resource repositories and cache policy in domain caches. This is a source scaffold,
not a packaged `dotnet new` template.
