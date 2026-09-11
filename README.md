# Service.Api Template

A reusable .NET 8 REST service template backed by PostgreSQL and Redis. Item is
an example parent; Action is stored child data, not executable behavior. This is
a standalone reference/template, not the active product backend by default.

## Start here

| Guide | Purpose |
| --- | --- |
| [Architecture](docs/ARCHITECTURE.md) | Contracts, data flow, and design decisions |
| [Development](docs/DEVELOPMENT.md) | Containers, configuration, debugging, and contribution |
| [Testing](docs/TESTING.md) | Test placement, isolation, and advanced verification |

## Prerequisites

Normal Make workflows require Git, running Docker Engine/Desktop with Compose v2+
and GNU Make 3.81+ in a Bash-compatible macOS/Linux environment. No host .NET or
Python is required. Docker must be able to pull images and restore NuGet packages.
No command installs system software. Native PowerShell/BSD Make are not supported.

The SDK is pinned to 8.0.303 by `global.json`; EF is pinned by
`.config/dotnet-tools.json`. Both run in the Dockerfile tooling stage. Host .NET is
optional for IDE/raw workflows; Python 3 is only needed for advanced certification.

## Quick Start

Run from the repository root:

```bash
make help
make setup
make migrate
make run
```

All environment-aware commands default to `ENV=local`. Setup creates missing
`.env.local` and `.env.dev`, preserves existing files, and starts no services.
Migrate starts the selected PostgreSQL and applies existing migrations in an SDK
container. Run builds/starts API, PostgreSQL and Redis containers and waits for
`/ready` to return **Healthy**. It never applies migrations automatically.

Open **http://127.0.0.1:5080/swagger**. LOCAL runs in Development with source mounts
and `dotnet watch`; commands return while the containers continue in the background.
Use `make logs` to follow API output; Ctrl-C stops log following, not the API.

### Try the API

In Swagger, `POST /items` with `{ "name": "My first item" }`. Expect 201 and copy
the returned ID. Retrieve it with `GET /items/{itemId}`. Create a nested Action via
`POST /items/{itemId}/actions` with `{ "name": "My first action", "type": "create" }`.
Expect 201 and retrieve it through `GET /actions/{actionId}`.

Both `/health` and `/ready` should return Healthy. Swagger JSON is available at
`/swagger/v1/swagger.json`. Health/readiness semantics are unchanged: readiness
checks database/schema and cache availability; startup does not migrate.

### Test and stop

```bash
make unit
make integration
make test
make stop
```

Unit runs only the unit project in an SDK container, without PostgreSQL/Redis.
Integration runs only the integration project with isolated real TEST providers.
Test runs the entire solution (unit + integration). TEST resources are disposable
and cleaned automatically; LOCAL/DEV databases are never reused.

Stop preserves PostgreSQL data. Restart with `make run`; migrations are needed
only for first setup or intentional schema updates. No public reset target exists.

## LOCAL / DEV / TEST

| Mode | Runtime | Configuration |
| --- | --- | --- |
| LOCAL | Developer SDK container, mounted source, watch/restart | Development; API 5080 |
| DEV | Published non-root runtime image, no source mount | Staging; API 18080 |
| TEST | SDK test container plus disposable providers when needed | Testing; isolated per run |

```bash
make build ENV=dev
make migrate ENV=dev
make run ENV=dev
make logs ENV=dev
make stop ENV=dev
```

Both LOCAL and DEV run on your machine. DEV is built-image verification, not a
remote deployment. Run rebuilds the selected image; build alone starts nothing.

## Command reference

```text
make help
make setup
make build [ENV=local|dev]
make run [ENV=local|dev]
make stop [ENV=local|dev]
make logs [ENV=local|dev]
make migrate [ENV=local|dev]
make unit
make integration
make test
```

Use `LOGS_ALL=1 make logs` for dependency logs too. Unsupported environments are
rejected. Advanced image smoke, workflow certification, guarded reset, raw tools,
and future parent-repository reuse are documented in [Development](docs/DEVELOPMENT.md).

## Architecture at a glance

```text
HTTP Request → Controller → Service → Repository → PostgreSQL
                                └→ Domain Cache → Redis

Model → service DTO → Mapper → Response → HTTP
```

Controllers bind HTTP and invoke response mappers. Services validate and orchestrate;
repositories own EF/PostgreSQL operations; domain caches own resource keys/TTL.
Read the [POST walkthrough](docs/ARCHITECTURE.md#post-items-walkthrough) and
[cached GET walkthrough](docs/ARCHITECTURE.md#get-itemsid-walkthrough) to follow actual methods.
