# Service.Api Template

A reusable **.NET 8 REST service template/example**, built with ASP.NET Core,
backed by PostgreSQL and optionally accelerated by Redis. It demonstrates two
resources: **Item** is an example parent; **Action** is a child belonging to an
Item. Action is stored data, not executable program behavior. This is a starting
point for a service, not a complete business application.

## Start here

**NEW CONTRIBUTORS SHOULD START WITH LOCAL.** Follow the Quick Start below, then
trace one request in the architecture guide.

| Guide | Read it for |
| --- | --- |
| [Architecture](docs/ARCHITECTURE.md) | Data shapes, DI, folders, request walkthroughs, contracts and design decisions |
| [Development](docs/DEVELOPMENT.md) | LOCAL/DEV operation, configuration, adding a domain, migrations, contribution and troubleshooting |
| [Testing](docs/TESTING.md) | Test placement, helper selection, isolation rules and verification commands |

Ready to change code? Jump to [add a domain](docs/DEVELOPMENT.md#add-a-domain-widget),
[migrations](docs/DEVELOPMENT.md#do-i-need-a-migration), [testing](docs/TESTING.md),
or [troubleshooting](docs/DEVELOPMENT.md#troubleshooting).

## Prerequisites

- **.NET SDK 8.0.303**, exactly as pinned in [global.json](global.json). Roll-forward
  is disabled; another .NET 8 SDK alone does not satisfy this pin.
- **Docker Engine/Desktop running**, with **Compose v2**. Compose pulls PostgreSQL
  17 Alpine and Redis 7.4 Alpine for you.
- **Git** to clone the repository.
- **Bash-compatible shell on macOS/Linux** for these commands. They are not native
  PowerShell commands. The workflow certification runner also uses POSIX signals.
- **Python 3** for smoke/workflow certification. Optional command-line HTTP checks
  use host `curl`; the browser/Swagger works for the initial interaction.

## Quick Start

Clone using this repository's Git URL, or open your existing checkout. Open two
terminals at the repository root—the directory containing `Service.sln`.
Commands below use LOCAL defaults; if you already have custom settings, see
[configuration](docs/DEVELOPMENT.md#configuration-sources) before proceeding.

### Terminal 1: check tools and start dependencies

```bash
dotnet --version
docker compose version
dotnet tool restore
dotnet restore
# Copy only if absent; never overwrite an existing developer configuration.
[ -f .env.local ] || cp .env.example .env.local
docker compose --env-file .env.local -f docker/compose.local.yml config --quiet
docker compose --env-file .env.local -f docker/compose.local.yml up -d --wait --wait-timeout 60
```

Expect SDK `8.0.303`, successful restores, and healthy PostgreSQL/Redis containers.
Compose starts them in the background, so this terminal becomes available again.
Stop here and use [troubleshooting](docs/DEVELOPMENT.md#troubleshooting) if a step fails.

### Terminal 2: configure, migrate, and run the host API

Use a fresh shell at the same repository root. Source only your trusted local file;
.NET itself does not load dotenv files.

```bash
set -a
. ./.env.local
set +a
unset DOTNET_ENVIRONMENT OpenApi__Enabled
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Postgres="Host=127.0.0.1;Port=${POSTGRES_PORT:-55432};Database=${POSTGRES_DB:-service_local};Username=${POSTGRES_USER:-service};Password=${POSTGRES_PASSWORD}"
export ConnectionStrings__Redis="127.0.0.1:${REDIS_PORT:-56379},connectTimeout=1000,asyncTimeout=1000,connectRetry=0"
export Cache__KeyPrefix=service-local
dotnet ef database update --project src/Service.Api
dotnet run --project src/Service.Api --launch-profile Service.Api
```

The migration command applies the existing schema or reports it is current.
The API should report **Development** and listen on **http://127.0.0.1:5080**.
Keep Terminal 2 running. API startup does not apply migrations automatically.

### Browser: send your first request

Open [Swagger UI](http://127.0.0.1:5080/swagger). Expand `POST /items`, choose
**Try it out**, enter this body and execute:

```json
{ "name": "My first item" }
```

Expect **201 Created** with a response containing `id`. Copy that value into
`GET /items/{itemId}` and execute; expect **200 OK** and the saved Item.
The creation response's `Location` header also identifies that GET route.

Open [/health](http://127.0.0.1:5080/health) and [/ready](http://127.0.0.1:5080/ready):
both should return **Healthy** when PostgreSQL/schema and Redis are available.
`Degraded` readiness means Redis is unavailable; database-backed CRUD may still work.
The [OpenAPI JSON](http://127.0.0.1:5080/swagger/v1/swagger.json) describes the API
that Swagger UI displays.

### Terminal 1: run tests and stop safely

With the host API still running in Terminal 2, use Terminal 1 at the repository root:

```bash
dotnet test tests/Service.Api.UnitTests/Service.Api.UnitTests.csproj
./scripts/test.sh
```

Expect passing unit tests, then a passing full solution using separate disposable
TEST infrastructure. The script removes its own resources; LOCAL remains running.
No manual `.env.test` setup is required for the default disposable credentials.

When finished, press **Ctrl-C in Terminal 2**, then stop LOCAL dependencies in Terminal 1:

```bash
docker compose --env-file .env.local -f docker/compose.local.yml down
```

PostgreSQL data survives this normal stop. See [LOCAL lifecycle](docs/DEVELOPMENT.md#local)
for logs, restart, IDE/watch operation and explicitly destructive reset instructions.
Do not commit real credentials; `.env.local` is ignored by Git and Docker.

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

## LOCAL / DEV / TEST

| Workflow | API / dependencies | Environment and purpose |
| --- | --- | --- |
| LOCAL | Host/debugger API; PostgreSQL/Redis in Docker | Development; preferred coding path |
| DEV | API, PostgreSQL and Redis in Compose | Staging; mock-production/background service while another service is debugged |
| TEST | Automated isolated hosts/scripts; disposable infrastructure | Testing hosts; separate image smoke uses Staging |

The same root Dockerfile serves DEV and smoke validation. LOCAL uses no API
container. Detailed commands and persistence rules are in [Development](docs/DEVELOPMENT.md).

## Command chooser

Run commands from the repository root.

| Need | Command / workflow |
| --- | --- |
| Run API while coding | [LOCAL Quick Start](#quick-start); optionally `dotnet watch --project src/Service.Api run` after setup |
| Run background/mock-production service | [DEV Compose workflow](docs/DEVELOPMENT.md#dev) |
| Fast isolated tests | `dotnet test tests/Service.Api.UnitTests/Service.Api.UnitTests.csproj` |
| Full automated suite | `./scripts/test.sh` |
| Built-image and HTTP smoke validation | `./scripts/smoke.sh` |
| LOCAL/DEV persistence and script-cleanup certification | `python3 scripts/certify-workflows.py` |

Workflow certification is an explicit broader check, not a prerequisite for every
small change. Read its [port, ownership and cleanup requirements](docs/DEVELOPMENT.md#workflow-certification)
before running it.
