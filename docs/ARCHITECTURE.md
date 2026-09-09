# Architecture

[Repository entry point](../README.md) · [Operation and contribution](DEVELOPMENT.md) · [Testing](TESTING.md)

For contributors learning the request flow or reviewing design tradeoffs, this guide
explains the current architecture and its boundaries.

Read [the glossary](#glossary) and [request walkthroughs](#post-items-walkthrough)
first if these layers are new to you. Experienced readers can jump to
[boundaries](#boundaries-and-folder-responsibilities) and [design decisions](#current-design-decisions).
To implement a change, see [add a domain](DEVELOPMENT.md#add-a-domain-widget),
[migrations](DEVELOPMENT.md#do-i-need-a-migration), [test placement](TESTING.md#test-placement)
or [troubleshooting](DEVELOPMENT.md#troubleshooting).

## Mental model

This service receives an HTTP request, coordinates work, and returns an HTTP response.
PostgreSQL is the source of truth. Redis can answer some reads faster, but a cache
failure does not replace the database with an error. Item is an example parent
resource; Action is its child. Neither represents a complete business application.

```text
HTTP → Controller → Service → Repository → ServiceDbContext → PostgreSQL
                         └→ Domain Cache → ICache / RedisCache → Redis

Persistence Model → service DTO → Mapper → public Response → HTTP
```

A cache hit starts the return path at the DTO. Controllers invoke the response
mappers; services construct DTOs from persistence models.

## Glossary

| Term | Meaning in this repository | Example |
| --- | --- | --- |
| Request | HTTP input and its validation rules | CreateItemRequest |
| Model | Entity persisted by EF Core; may have navigation relationships | ItemModel |
| DTO | Data transfer object used by services and typed cache payloads | ItemDto |
| Response | Public HTTP output shape | ItemResponse |
| Controller | Binds HTTP, calls a service, invokes a mapper, selects the HTTP result | ItemsController |
| Service | Validates and coordinates persistence/cache work | ItemService |
| Repository | Owns EF queries and saves; returns models | ItemRepository |
| ServiceDbContext | Scoped EF database session: tracks entity changes and saves them | Items and Actions DbSets |
| Domain Cache | Owns a resource's cache key and TTL policy | ItemCache |
| ICache | Interface for generic typed cache operations | GetAsync, SetAsync, RemoveAsync |
| RedisCache | Implements generic serialization, provider calls, time budgets and fallback | Infrastructure/Cache/RedisCache.cs |
| Mapper | Converts service DTOs to HTTP responses | ItemMapper.ToResponse |
| Health | Whether the API process can answer a liveness request | /health |
| Readiness | Whether required database/schema and optional cache are available | /ready |

The four data shapes have separate jobs even when their fields overlap. A caller
supplies a **Request**; EF persists a **Model**; a service returns a **DTO**; a
controller sends a **Response**. This prevents EF navigation graphs or persistence
concerns from becoming the public API contract.

## Repository structure and existing contracts

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

### Persistence and naming invariants

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

### Logging and exceptions

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
`NotFoundException`. IDs remain internal read-only properties; HTTP details use safe messages
without resource IDs. Missing parents in Action workflows use ItemNotFoundException.
Typed Create/Update validation overloads reuse DataAnnotations through a private
ValidateAnnotations helper for direct callers outside MVC. RequestValidationException
remains shared, including the empty Action parent-ID check.

## Boundaries and folder responsibilities

Paths below are relative to `src/Service.Api/`.

| Folder | What belongs here | Boundary |
| --- | --- | --- |
| Controllers/ | Routes, binding, service calls, response mapper calls, HTTP status metadata | No direct DbContext or Redis access |
| Dtos/ | Service/cache DTOs, Requests/, Responses/ in singular domain folders | No EF navigation graphs in service DTOs |
| Enums/ | Domain classifications and their defined values | JSON and database representations are intentional contracts |
| Exceptions/ | Domain errors and the central API exception handler | Public error details must remain safe |
| Extensions/ | Detailed API, database and cache registrations | Program keeps high-level composition and pipeline visible |
| Infrastructure/Database/ | DbContext, EF configurations/migrations, repositories, PostgreSQL health | Persistence belongs behind repositories |
| Infrastructure/Cache/ | Generic adapter, domain wrappers, cache options and Redis health | Keys/TTL belong to domain wrappers, not services |
| Mappers/ | DTO → Response conversion | Controllers call these instead of repeating field mapping |
| Models/ | ItemModel, ActionModel and timestamped-entity contract | Models are not returned directly over HTTP |
| Services/ | Validation, orchestration, Model → DTO conversion | No DbContext, raw Redis calls or HTTP response construction |

`tests/Service.Api.UnitTests/` exercises isolated behavior; integration tests use
hosted HTTP and real infrastructure where required. See the
[operational test overview](DEVELOPMENT.md#test-overview) for running them.

## Dependency injection and composition

Constructor injection means a class asks for its dependencies as constructor
parameters rather than constructing them itself. ASP.NET Core supplies registered
implementations. For example, ItemsController asks for IItemService and IActionService;
[Program.cs](../src/Service.Api/Program.cs) registers:

```csharp
builder.Services.AddScoped<IItemService, ItemService>();
builder.Services.AddScoped<IActionService, ActionService>();
```

A **scoped** instance is shared within one scope, normally one HTTP request.
ServiceDbContext, domain repositories and domain services are scoped. A **singleton**
is shared across the application host. ICache/RedisCache and the domain caches are
singletons; they do not hold request-specific state or a scoped DbContext.

| Registration group | Implementation | Responsibility |
| --- | --- | --- |
| AddApi() | [ApiExtensions](../src/Service.Api/Extensions/ApiExtensions.cs) | Controllers, JSON enums, Problem Details, exception handler, Swagger generation |
| AddDatabase(configuration) | [DatabaseExtensions](../src/Service.Api/Extensions/DatabaseExtensions.cs) | Npgsql/DbContext, scoped repositories, PostgreSQL readiness |
| AddCache(configuration) | [CacheExtensions](../src/Service.Api/Extensions/CacheExtensions.cs) | Validated cache options, Redis provider, singleton caches, Redis readiness |
| Domain service registrations | [Program](../src/Service.Api/Program.cs) | Explicit service interface → implementation composition |

After building the app, Program emits the missing-Redis warning when applicable.
It installs exception handling, then status-code pages, then conditionally installs
Swagger middleware. It maps controllers, `/health`, and `/ready`, then runs the app.
Startup does not apply migrations or seed data.

## POST /items walkthrough

Send this body through Swagger's `POST /items` operation:

```json
{ "name": "My first item" }
```

| Step / actual file and method | Contribution |
| --- | --- |
| [CreateItemRequest](../src/Service.Api/Dtos/Item/Requests/CreateItemRequest.cs) | Defines required Name with the 200-character limit; MVC checks HTTP input |
| [ItemsController.Create](../src/Service.Api/Controllers/ItemsController.cs) | Passes the request and cancellation token to IItemService |
| [ItemService.CreateAsync](../src/Service.Api/Services/ItemService.cs) | Validates again for non-HTTP callers, constructs ItemModel and awaits persistence |
| [ItemRepository.CreateAsync](../src/Service.Api/Infrastructure/Database/Repositories/Item/ItemRepository.cs) | Adds the entity to the DbContext and awaits SaveChangesAsync |
| [ServiceDbContext.SaveChangesAsync](../src/Service.Api/Infrastructure/Database/ServiceDbContext.cs) | Stamps UTC timestamps; EF/Npgsql writes the row to PostgreSQL |
| ItemService's ToDto | Builds ItemDto from the saved model; creation does not populate cache |
| [ItemMapper.ToResponse](../src/Service.Api/Mappers/ItemMapper.cs) | Converts the DTO into ItemResponse |
| ItemsController.Create | Returns 201 Created and a Location pointing to `/items/{itemId}` |

The model supplies the UUID. The response includes `id`, `name`, `status`,
`createdAt`, and `updatedAt`; a new Item has status `active`.

**Breakpoints:** start at ItemsController.Create, then ItemService.CreateAsync,
ItemRepository.CreateAsync, ServiceDbContext.SaveChangesAsync and
ItemMapper.ToResponse. Continue execution before judging readiness timeouts; a
paused debugger can delay other requests.

## GET /items/{id} walkthrough

Use the `id` returned by POST in Swagger's GET operation.

1. [ItemsController.Get](../src/Service.Api/Controllers/ItemsController.cs) calls ItemService.GetAsync.
2. [ItemService.GetAsync](../src/Service.Api/Services/ItemService.cs) asks ItemCache for that ID.
3. [ItemCache.GetAsync](../src/Service.Api/Infrastructure/Cache/Item/ItemCache.cs) constructs `{prefix}:items:{id:D}` and calls ICache.GetAsync&lt;ItemDto&gt;.
4. [RedisCache.GetAsync](../src/Service.Api/Infrastructure/Cache/RedisCache.cs) reads through the distributed-cache provider and deserializes the payload.
5. **Hit:** the service returns the cached ItemDto without loading the Item from PostgreSQL.
6. **Miss:** the service awaits ItemRepository.GetByIdAsync, which performs a no-tracking query. A missing row becomes ItemNotFoundException. Otherwise the service maps the model to ItemDto and awaits ItemCache.SetAsync before returning it.
7. The controller invokes ItemMapper.ToResponse and returns 200 with ItemResponse.

Invalid payloads and provider failures fall back to the database. Caller
cancellation remains cancellation rather than a cache miss. The provider adapter
bounds each generic cache operation to two seconds.

**Breakpoints:** ItemsController.Get → ItemService.GetAsync → ItemCache.GetAsync →
RedisCache.GetAsync. On the first uncached read, follow ItemRepository.GetByIdAsync
and ItemCache.SetAsync. On a repeat read, the Item repository lookup may not run.
An Action cache hit is different: it still checks row existence, as described below.

## Current design decisions

| Decision | Reason in this template |
| --- | --- |
| Repository interfaces instead of DbContext in services | Keep EF query/save behavior in one layer; service orchestration can be tested with controlled dependencies |
| Domain cache wrappers | Keep resource keys and TTL policy out of services and the generic adapter |
| DTO separate from Response | Service/cache data and HTTP contracts can evolve deliberately without exposing EF entities |
| Model → DTO in services | Services return their own data contract after coordinating dependencies |
| DTO → Response in mappers | Controllers reuse explicit public-response mapping |
| Redis optional | PostgreSQL remains authoritative; cache failure permits database fallback and degraded readiness |
| Explicit migrations | Operators choose the target database and when existing schema changes are applied |
| No generic repository currently needed | Concrete interfaces express the actual Item/Action queries and writes |
| No custom UnitOfWork currently needed | Repository writes already save all pending changes in the scoped DbContext |
| No custom middleware currently needed | Built-in exception handling, status-code pages and endpoint handling cover the current pipeline |

These are current design decisions, not permanent prohibitions on future reviewed
requirements. Preserve the existing boundaries when working with the current design.


## Item / Action relationship and persistence

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

Nested creation/listing lives under `/items/{itemId}/actions`. CreateItemActionRequest
has no ItemId: the controller constructs the service request using the route's parent
ID. PUT cannot change Action ownership. PostgreSQL cascades Action deletion when
the parent is deleted; an Action read rejects a stale cached child whose row is gone.

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

## Cache behavior and consistency

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
