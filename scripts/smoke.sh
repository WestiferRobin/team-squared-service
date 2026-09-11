#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
# Opt-in workflow certification only; ordinary runs leave this unset.
case "${SERVICE_WORKFLOW_CERTIFICATION:-}" in
  ""|fail|term) ;;
  *) echo 'SERVICE_WORKFLOW_CERTIFICATION must be unset, fail, or term.' >&2; exit 2 ;;
esac
for command in docker python3; do
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
project="service-smoke-$(date +%s)-$$-$RANDOM"
api_container="${project}-api"
image="service-api:${project}"
tool_image="${project}-tooling"
tool_container="${project}-tooling"
compose=(docker compose --env-file /dev/null -p "$project" -f docker/compose.test.yml)
cleanup() {
  local status=$?
  trap - EXIT INT TERM
  if (( status != 0 )); then
    echo "Dependency logs ($project):" >&2
    "${compose[@]}" logs --no-color --tail 100 >&2 || true
    echo "API logs ($api_container):" >&2
    docker logs --tail 100 "$api_container" >&2 || true
  fi
  docker rm -f "$tool_container" >/dev/null 2>&1 || true
  if docker image inspect "$tool_image" >/dev/null 2>&1; then
    docker image rm "$tool_image" >/dev/null || { if (( status == 0 )); then status=1; fi; }
  fi
  if docker container inspect "$api_container" >/dev/null 2>&1; then
    docker rm -f "$api_container" >/dev/null || { if (( status == 0 )); then status=1; fi; }
  fi
  if ! "${compose[@]}" down --volumes --remove-orphans; then
    echo "Cleanup failed for owned project $project" >&2
    if (( status == 0 )); then status=1; fi
  fi
  if docker image inspect "$image" >/dev/null 2>&1; then
    docker image rm "$image" >/dev/null || { if (( status == 0 )); then status=1; fi; }
  fi
  exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
"${compose[@]}" config --quiet
"${compose[@]}" up -d --wait --wait-timeout 60
export ConnectionStrings__Postgres="Host=postgres;Port=5432;Database=service_test;Username=$POSTGRES_USER;Password=$POSTGRES_PASSWORD"
export ConnectionStrings__Redis="redis:6379,connectTimeout=1000,asyncTimeout=1000,connectRetry=0"
docker build --target tooling -t "$tool_image" .
docker run --rm --name "$tool_container" --network "${project}_default" \
  -e ConnectionStrings__Postgres -e ASPNETCORE_ENVIRONMENT=Testing \
  "$tool_image" dotnet ef database update --project src/Service.Api
# The same root Dockerfile serves DEV and this disposable image certification.
docker build -t "$image" .
export ConnectionStrings__Postgres="Host=postgres;Port=5432;Database=service_test;Username=$POSTGRES_USER;Password=$POSTGRES_PASSWORD"
export ConnectionStrings__Redis="redis:6379,connectTimeout=1000,asyncTimeout=1000,connectRetry=0"
docker run -d --name "$api_container" --network "${project}_default" \
  -p 127.0.0.1::8080 -e ASPNETCORE_ENVIRONMENT=Staging -e OpenApi__Enabled=true \
  -e ConnectionStrings__Postgres -e ConnectionStrings__Redis -e Cache__KeyPrefix "$image"
api_address=$(docker port "$api_container" 8080/tcp)
export API_BASE="http://127.0.0.1:${api_address##*:}"
python3 - <<'PY'
import json, os, time, urllib.error, urllib.request
from datetime import datetime
base = os.environ['API_BASE']
def request(method, path, expected, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(base + path, data=data, method=method,
                                 headers={'Content-Type': 'application/json'})
    try:
        response = urllib.request.urlopen(req, timeout=5)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        payload = response.read().decode()
        assert response.status == expected, (method, path, response.status, payload)
        return payload, response.headers

def record(payload):
    value = json.loads(payload)
    # PostgreSQL stores microseconds; the creation response can contain .NET ticks.
    # Compare timestamps at database precision while checking every response field.
    for key in ('createdAt', 'updatedAt'):
        value[key] = datetime.fromisoformat(value[key].replace('Z', '+00:00'))
    return value

deadline = time.monotonic() + 60
last = 'API has not responded'
while time.monotonic() < deadline:
    try:
        body, _ = request('GET', '/ready', 200)
        if body == 'Healthy':
            break
        last = body
    except (OSError, AssertionError) as error:
        last = str(error)
    time.sleep(.5)
else:
    raise RuntimeError(f'Readiness did not become fully Healthy within 60 seconds: {last}')
assert request('GET', '/health', 200)[0] == 'Healthy'
assert request('GET', '/ready', 200)[0] == 'Healthy'
doc = json.loads(request('GET', '/swagger/v1/swagger.json', 200)[0])
assert all(path in doc['paths'] for path in
           ['/items', '/items/{itemId}', '/items/{itemId}/actions', '/actions', '/actions/{actionId}'])
assert 'swagger-ui' in request('GET', '/swagger/index.html', 200)[0]
item_body, item_headers = request('POST', '/items', 201, {'name': 'Smoke item'})
item = record(item_body)
item_path = '/items/' + item['id']
assert item_headers['Location'].endswith(item_path)
assert item['status'] == 'active'
action_body, headers = request('POST', item_path + '/actions', 201,
                               {'name': 'Smoke action', 'type': 'create'})
action = record(action_body)
action_path = '/actions/' + action['id']
assert headers['Location'].endswith(action_path)
assert action['itemId'] == item['id']
for _ in range(2):  # Exercise repeat reads with the real cache enabled.
    assert record(request('GET', action_path, 200)[0]) == action
    assert record(request('GET', item_path, 200)[0]) == item
updated = record(request('PUT', action_path, 200, {'name': 'Updated action', 'type': 'update'})[0])
assert updated['type'] == 'update'
assert record(request('GET', action_path, 200)[0]) == updated
assert len(json.loads(request('GET', item_path + '/actions', 200)[0])) == 1
assert any(row['id'] == action['id'] for row in json.loads(request('GET', '/actions', 200)[0]))
assert any(row['id'] == item['id'] for row in json.loads(request('GET', '/items', 200)[0]))
updated_item = record(request('PUT', item_path, 200, {'name': 'Updated item', 'status': 'archived'})[0])
assert updated_item['status'] == 'archived'
assert record(request('GET', item_path, 200)[0]) == updated_item
# Cascade deletion must also reject the previously cached Action.
assert request('DELETE', item_path, 204)[0] == ''
request('GET', item_path, 404)
request('GET', action_path, 404)
print('Docker smoke passed: Healthy liveness/readiness, Swagger JSON/UI, Item/Action CRUD, cache reads and cascade.')
PY

# Deterministic certification checkpoint after provisioning (default OFF).
echo "Workflow resources: $project"
case "${SERVICE_WORKFLOW_CERTIFICATION:-}" in
  fail) echo 'Certification: controlled command failure (73).' >&2; (exit 73) ;;
  term) echo 'Certification: delivering SIGTERM at the provisioned checkpoint.' >&2; kill -TERM "$$" ;;
esac
