#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
case "${ENV-local}" in local|dev) ;; *) echo "Unsupported ENV=${ENV}. Use local or dev." >&2; exit 2 ;; esac
# Select a project without duplicating provider provisioning or cleanup.
case "${1:-all}" in
  all) target=Service.sln ;;
  unit) target=tests/Service.Api.UnitTests/Service.Api.UnitTests.csproj ;;
  integration) target=tests/Service.Api.IntegrationTests/Service.Api.IntegrationTests.csproj ;;
  *) echo 'Usage: scripts/test.sh [all|unit|integration]' >&2; exit 2 ;;
esac
[[ $# -le 1 ]] || { echo 'Usage: scripts/test.sh [all|unit|integration]' >&2; exit 2; }
# Opt-in workflow certification only; ordinary runs leave this unset.
case "${SERVICE_WORKFLOW_CERTIFICATION:-}" in
  ""|fail|term) ;;
  *) echo 'SERVICE_WORKFLOW_CERTIFICATION must be unset, fail, or term.' >&2; exit 2 ;;
esac
for command in docker; do
  command -v "$command" >/dev/null || { echo "Required command missing: $command" >&2; exit 1; }
done

# Read only simple disposable credentials; never execute an env file as shell code.
POSTGRES_USER=service
POSTGRES_PASSWORD=change_me_test_only
if [[ -f .env.test ]]; then
  while IFS='=' read -r key value || [[ -n "$key" ]]; do
    value=${value%$'\r'}
    case "$key" in
      POSTGRES_USER) POSTGRES_USER=$value ;;
      POSTGRES_PASSWORD) POSTGRES_PASSWORD=$value ;;
    esac
  done < .env.test
fi
for value in "$POSTGRES_USER" "$POSTGRES_PASSWORD"; do
  [[ "$value" =~ ^[a-zA-Z0-9_.-]+$ ]] || {
    echo '.env.test credentials must contain only letters, digits, underscore, dot or dash (no quotes).' >&2
    exit 1
  }
done
# Override inherited LOCAL/DEV configuration. Docker assigns available host ports.
export POSTGRES_USER POSTGRES_PASSWORD POSTGRES_DB=service_test POSTGRES_PORT=0 REDIS_PORT=0
export ASPNETCORE_ENVIRONMENT=Testing Cache__KeyPrefix=service-test
unset DOTNET_ENVIRONMENT OpenApi__Enabled
project="service-test-$(date +%s)-$$-$RANDOM"
tool_image="$project-tooling"
tool_container="$project-tooling"
compose=(docker compose --env-file /dev/null -p "$project" -f docker/compose.test.yml)
cleanup() {
  local status=$?
  trap - EXIT INT TERM
  if (( status != 0 )); then "${compose[@]}" logs --no-color --tail 100 >&2 || true; fi
  docker rm -f "$tool_container" >/dev/null 2>&1 || true
  if docker image inspect "$tool_image" >/dev/null 2>&1; then
    docker image rm "$tool_image" >/dev/null || { if (( status == 0 )); then status=1; fi; }
  fi
  if ! "${compose[@]}" down --volumes --remove-orphans; then
    echo "Cleanup failed for owned project $project" >&2
    if (( status == 0 )); then status=1; fi
  fi
  exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
docker build --target tooling -t "$tool_image" .
if [[ ${1:-all} != unit ]]; then
  "${compose[@]}" config --quiet
  "${compose[@]}" up -d --wait --wait-timeout 60
fi
export ConnectionStrings__Postgres="Host=postgres;Port=5432;Database=service_test;Username=$POSTGRES_USER;Password=$POSTGRES_PASSWORD"
export ConnectionStrings__Redis="redis:6379,connectTimeout=1000,asyncTimeout=1000,connectRetry=0"
# Deterministic certification checkpoint after provisioning (default OFF).
echo "Workflow resources: $project"
case "${SERVICE_WORKFLOW_CERTIFICATION:-}" in
  fail) echo 'Certification: controlled command failure (73).' >&2; (exit 73) ;;
  term) echo 'Certification: delivering SIGTERM at the provisioned checkpoint.' >&2; kill -TERM "$$" ;;
esac
echo "Test target: $target"
network=(--network none)
environment=(-e ASPNETCORE_ENVIRONMENT=Testing -e Cache__KeyPrefix=service-test)
if [[ ${1:-all} != unit ]]; then
  network=(--network "${project}_default")
  environment+=(-e ConnectionStrings__Postgres -e ConnectionStrings__Redis)
fi
docker run --rm --name "$tool_container" "${network[@]}" "${environment[@]}" \
  "$tool_image" dotnet test "$target" --no-restore
