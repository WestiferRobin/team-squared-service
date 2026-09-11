#!/usr/bin/env bash
# Standalone container lifecycle. Configuration is parsed as data; migrations stay explicit.
set -euo pipefail
cd "$(dirname "$0")/.."
action=${1:?Missing action}
mode=${2-local}
project=${3:-}
confirmation=${4:-}
fail() { echo "Error: $*" >&2; exit 2; }
require() { command -v "$1" >/dev/null || fail "Missing $1. See README prerequisites; no software is installed automatically."; }

case "$mode" in local|dev) ;; *) fail "Unsupported ENV=$mode. Use local or dev." ;; esac
if [[ "$action" == setup ]]; then
  require docker
  require make
  make_version=$(make --version)
  [[ "$make_version" =~ GNU\ Make\ ([0-9]+)\.([0-9]+) ]] || fail 'GNU Make 3.81+ is required.'
  (( BASH_REMATCH[1] > 3 || (BASH_REMATCH[1] == 3 && BASH_REMATCH[2] >= 81) )) || fail 'GNU Make 3.81+ is required.'
  docker compose version || fail 'Docker Compose v2+ is required.'
  docker info >/dev/null || fail 'Start Docker Engine/Desktop, then retry make setup.'
  for file in .env.local .env.dev; do
    if [[ -e "$file" || -L "$file" ]]; then
      echo "Preserved $file"
    else
      (set -o noclobber; cat .env.example > "$file")
      echo "Created $file with disposable developer defaults"
    fi
  done
  echo 'Next: make migrate; make run. No services started. Host .NET is optional.'
  exit 0
fi
case "$action" in build|run|migrate|stop|logs|reset) ;; *) fail "Unknown action: $action" ;; esac
project=${project:-service-$mode}
# Keep reset/stop away from TEST and unrelated projects, including accidental overrides.
case "$project" in "service-$mode"|"service-$mode-"*) ;; *) fail "PROJECT must be service-$mode or begin service-$mode-." ;; esac
[[ "$project" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || fail 'Invalid Compose project name.'
file=".env.$mode"
[[ -f "$file" ]] || fail "Missing $file. Run make setup first."

# Parse a deliberately small dotenv subset, never execute developer configuration.
# File values/defaults are authoritative here, not inherited LOCAL/DEV exports.
POSTGRES_USER=service
POSTGRES_PASSWORD=
POSTGRES_DB="service_$mode"
if [[ "$mode" == local ]]; then POSTGRES_PORT=55432; REDIS_PORT=56379; API_PORT=5080
else POSTGRES_PORT=25432; REDIS_PORT=26379; API_PORT=18080; fi
while IFS= read -r line || [[ -n "$line" ]]; do
  line=${line%$'\r'}
  [[ "$line" =~ ^[[:space:]]*(#.*)?$ ]] && continue
  [[ "$line" == *=* ]] || fail "Invalid assignment in $file; use unquoted KEY=value."
  key=${line%%=*}; value=${line#*=}
  case "$key" in POSTGRES_USER|POSTGRES_PASSWORD|POSTGRES_DB|POSTGRES_PORT|REDIS_PORT|API_PORT) ;;
    *) fail "Unsupported key $key in $file; see the Make environment reference." ;; esac
  [[ "$value" =~ ^[a-zA-Z0-9_.-]+$ ]] || fail "Invalid $key in $file; use nonempty unquoted letters, digits, underscore, dot or dash."
  printf -v "$key" '%s' "$value"
done < "$file"
[[ -n "$POSTGRES_PASSWORD" ]] || fail "POSTGRES_PASSWORD is required in $file."
for port in "$POSTGRES_PORT" "$REDIS_PORT" "$API_PORT"; do
  [[ "$port" =~ ^[0-9]{1,5}$ ]] && (( 10#$port >= 1 && 10#$port <= 65535 )) || fail 'Ports must be integers from 1 to 65535.'
done
export POSTGRES_USER POSTGRES_PASSWORD POSTGRES_DB POSTGRES_PORT REDIS_PORT API_PORT
compose=(docker compose --env-file "$file" -p "$project" -f "docker/compose.$mode.yml")
tool_image="$project-tooling"
build_tools() { docker build --target tooling -t "$tool_image" .; }
tool_container="$project-tool-$$-$RANDOM"
cleanup_tool() { docker rm -f "$tool_container" >/dev/null 2>&1 || true; }
trap cleanup_tool EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
wait_ready() {
  build_tools
  docker run --rm --name "$tool_container" --network "${project}_default" "$tool_image" bash -c '
    for ((i=0; i<120; i++)); do
      body=$(curl -fsS --max-time 2 http://service-api:8080/ready 2>/dev/null) && [[ "$body" == Healthy ]] && exit 0
      sleep 1
    done
    exit 1
  ' || { echo "Readiness failed. Run: make migrate ENV=$mode PROJECT=$project; inspect: make logs ENV=$mode PROJECT=$project" >&2; return 1; }
}
case "$action" in
  build) "${compose[@]}" build service-api ;;
  run)
    echo "Migrations are explicit: make migrate ENV=$mode PROJECT=$project"
    "${compose[@]}" up -d --build service-api
    wait_ready
    echo "API ready: http://127.0.0.1:$API_PORT"
    echo "Swagger: http://127.0.0.1:$API_PORT/swagger" ;;
  migrate)
    "${compose[@]}" up -d --wait --wait-timeout 60 postgres
    build_tools
    docker run --rm --name "$tool_container" --network "${project}_default" \
      -e "ASPNETCORE_ENVIRONMENT=$([[ "$mode" == local ]] && echo Development || echo Staging)" \
      -e "ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=$POSTGRES_DB;Username=$POSTGRES_USER;Password=$POSTGRES_PASSWORD" \
      "$tool_image" dotnet ef database update --project src/Service.Api ;;
  stop) "${compose[@]}" down ;;
  reset)
    echo "WARNING: DESTRUCTIVE RESET of $mode project $project. Its PostgreSQL volume and ALL database rows will be deleted."
    echo 'This cannot be undone.'
    if [[ "$confirmation" != delete ]]; then
      [[ -t 0 ]] || fail 'Confirmation required. Interactive: type delete; automation: CONFIRM=delete for an owned disposable project only.'
      read -r -p "Type delete to reset $project: " confirmation
      [[ "$confirmation" == delete ]] || fail 'Reset cancelled; no resources changed.'
    fi
    "${compose[@]}" down -v
    echo "Reset complete. Reapply migrations with make migrate ENV=$mode (retain PROJECT if overridden)." ;;
  logs)
    if [[ ${LOGS_ALL:-0} == 1 ]]; then exec "${compose[@]}" logs -f
    else exec "${compose[@]}" logs -f service-api; fi ;;
esac
