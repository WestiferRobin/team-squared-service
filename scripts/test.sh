#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
# Opt-in workflow certification only; ordinary runs leave this unset.
case "${SERVICE_WORKFLOW_CERTIFICATION:-}" in
  ""|fail|term) ;;
  *) echo 'SERVICE_WORKFLOW_CERTIFICATION must be unset, fail, or term.' >&2; exit 2 ;;
esac
for command in docker dotnet; do
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
compose=(docker compose --env-file /dev/null -p "$project" -f docker/compose.test.yml)
cleanup() {
  local status=$?
  trap - EXIT INT TERM
  if (( status != 0 )); then "${compose[@]}" logs --no-color --tail 100 >&2 || true; fi
  if ! "${compose[@]}" down --volumes --remove-orphans; then
    echo "Cleanup failed for owned project $project" >&2
    if (( status == 0 )); then status=1; fi
  fi
  exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
"${compose[@]}" config --quiet
"${compose[@]}" up -d --wait --wait-timeout 60
pg_address=$("${compose[@]}" port postgres 5432)
redis_address=$("${compose[@]}" port redis 6379)
export ConnectionStrings__Postgres="Host=127.0.0.1;Port=${pg_address##*:};Database=service_test;Username=$POSTGRES_USER;Password=$POSTGRES_PASSWORD"
export ConnectionStrings__Redis="127.0.0.1:${redis_address##*:},connectTimeout=1000,asyncTimeout=1000,connectRetry=0"
dotnet tool restore
dotnet restore
# Deterministic certification checkpoint after provisioning (default OFF).
echo "Workflow resources: $project"
case "${SERVICE_WORKFLOW_CERTIFICATION:-}" in
  fail) echo 'Certification: controlled command failure (73).' >&2; (exit 73) ;;
  term) echo 'Certification: delivering SIGTERM at the provisioned checkpoint.' >&2; kill -TERM "$$" ;;
esac
dotnet test Service.sln --no-restore
