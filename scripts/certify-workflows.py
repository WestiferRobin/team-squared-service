#!/usr/bin/env python3
"""Opt-in LOCAL/DEV/TEST certification using actual Compose files and owned resources."""
import json
import os
from pathlib import Path
import re
import signal
import socket
import subprocess
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
RUN = uuid.uuid4().hex[:12]
LOGS = ROOT / 'artifacts' / f'workflows-{RUN}'
LOGS.mkdir(parents=True)
# Child-process configuration only: never load a developer dotenv file or secrets.
ENV = {k: v for k, v in os.environ.items() if not k.startswith((
    'ConnectionStrings__', 'Cache__', 'Logging__', 'ASPNETCORE_', 'DOTNET_ENVIRONMENT',
    'OpenApi__', 'POSTGRES_', 'REDIS_', 'API_PORT', 'COMPOSE_', 'SERVICE_WORKFLOW_'))
    and k != 'AllowedHosts'}
ENV.update(POSTGRES_USER='service', POSTGRES_PASSWORD='workflow_test_only')
sequence = 0


def run(args, env=None, expected=0, timeout=300):
    global sequence
    sequence += 1
    path = LOGS / f'{sequence:03d}-{Path(args[0]).name}.log'
    process = subprocess.Popen(args, cwd=ROOT, env=env or ENV, text=True,
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT, start_new_session=True)
    try:
        output, _ = process.communicate(timeout=timeout)
    except (subprocess.TimeoutExpired, KeyboardInterrupt):
        # Give shell EXIT/TERM traps time to clean up rather than killing the shell immediately.
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGTERM)
        try:
            output, _ = process.communicate(timeout=30)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            output, _ = process.communicate(timeout=5)
        path.write_text(output)
        raise
    path.write_text(output)
    if process.returncode != expected:
        raise AssertionError(f'{args}: expected {expected}, got {process.returncode}; see {path}')
    return output


def inventory():
    return {kind: set(run(['docker', *command]).split()) for kind, command in {
        'containers': ['ps', '-aq'], 'networks': ['network', 'ls', '-q'],
        'volumes': ['volume', 'ls', '-q']}.items()}


def container_states(ids):
    if not ids:
        return {}
    return {row['Id']: (row['State']['Status'], row['State']['StartedAt'])
            for row in json.loads(run(['docker', 'inspect', *sorted(ids)]))}


def request(base, method, path, status=200, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(base + path, data=data, method=method,
                                 headers={'Content-Type': 'application/json'})
    try:
        response = urllib.request.urlopen(req, timeout=5)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        payload = response.read().decode()
        assert response.status == status, (method, path, status, response.status, payload)
        return payload, response.headers


def ready(base):
    deadline = time.monotonic() + 60
    last = ''
    while time.monotonic() < deadline:
        try:
            assert request(base, 'GET', '/ready')[0] == 'Healthy'
            return
        except (OSError, AssertionError) as error:
            last = str(error)
            time.sleep(.25)
    raise AssertionError(f'{base} did not become Healthy: {last}')


class Workflow:
    def __init__(self, mode):
        self.mode = mode
        self.project = f'service-cert-{mode}-{RUN}'
        self.env = dict(ENV)
        self.compose = ['docker', 'compose', '--env-file', '/dev/null', '-p', self.project,
                        '-f', f'docker/compose.{mode}.yml']
        self.api = None
        self.api_log = None
        self.created = False
        self.item = None
        self.base = f'http://127.0.0.1:{5080 if mode == "local" else 18080}'

    def call(self, *args):
        return run(self.compose + list(args), self.env)

    def sql(self, query):
        return self.call('exec', '-T', 'postgres', 'psql', '-U', 'service', '-d',
                         f'service_{self.mode}', '-Atc', query).strip()

    def redis(self, *args):
        return self.call('exec', '-T', 'redis', 'redis-cli', *args).strip()

    def config(self):
        config = json.loads(self.call('config', '--format', 'json'))
        services = config['services']
        assert set(services) == {'postgres', 'redis', 'service-api'}
        ports = {'postgres': 55432, 'redis': 56379, 'service-api': 5080} if self.mode == 'local' else {
            'postgres': 25432, 'redis': 26379, 'service-api': 18080}
        for name, service in services.items():
            assert 'container_name' not in service
            assert service['ports'][0]['host_ip'] == '127.0.0.1'
            assert int(service['ports'][0]['published']) == ports[name]
        assert services['postgres']['environment']['POSTGRES_DB'] == f'service_{self.mode}'
        assert services['postgres']['volumes'][0]['type'] == 'volume'
        assert services['redis']['tmpfs'] == ['/data']
        assert services['redis']['command'] == ['redis-server', '--save', '', '--appendonly', 'no']
        if self.mode == 'dev':
            api = services['service-api']
            assert api['build']['dockerfile'] == 'Dockerfile'
            assert api['ports'][0]['target'] == 8080
            assert api['environment']['ASPNETCORE_ENVIRONMENT'] == 'Staging'
            assert api['environment']['OpenApi__Enabled'] == 'true'
            assert api['environment']['Cache__KeyPrefix'] == 'service-dev'
            assert 'Host=postgres;' in api['environment']['ConnectionStrings__Postgres']
            assert api['environment']['ConnectionStrings__Redis'].startswith('redis:6379,')
            assert all(api['depends_on'][name]['condition'] == 'service_healthy'
                       for name in ('postgres', 'redis'))
            copied = self.compose.copy()
            copied[copied.index('-p') + 1] += '-copy'
            alternative = json.loads(run(copied + ['config', '--format', 'json'],
                dict(self.env, API_PORT='28080', POSTGRES_PORT='35432', REDIS_PORT='36379')))
            assert int(alternative['services']['service-api']['ports'][0]['published']) == 28080
            assert int(alternative['services']['postgres']['ports'][0]['published']) == 35432
            assert int(alternative['services']['redis']['ports'][0]['published']) == 36379
            assert alternative['volumes']['postgres-data']['name'] != config['volumes']['postgres-data']['name']
            assert alternative['networks']['default']['name'] != config['networks']['default']['name']
        return ports

    def start_api(self, build=False):
        self.call('up', '-d', *(['--build'] if build else []), 'service-api')
        api_id = self.call('ps', '-q', 'service-api').strip()
        actual = json.loads(run(['docker', 'inspect', api_id]))[0]
        environment = 'Development' if self.mode == 'local' else 'Staging'
        assert f'ASPNETCORE_ENVIRONMENT={environment}' in actual['Config']['Env']
        assert 'ASPNETCORE_HTTP_PORTS=8080' in actual['Config']['Env']
        if self.mode == 'local':
            assert any(m['Type'] == 'bind' and m['Destination'] == '/source/src' and not m['RW'] for m in actual['Mounts'])
            assert 'watch' in actual['Config']['Cmd']
        else:
            assert not actual['Mounts'], 'DEV runtime must have no source mounts'
            assert actual['Config']['User'] not in ('', '0', 'root')
        ready(self.base)
        for name in ('postgres', 'redis'):
            cid = self.call('ps', '-q', name).strip()
            assert json.loads(run(['docker', 'inspect', cid]))[0]['State']['Health']['Status'] == 'healthy'

    def stop_api(self):
        # Compose owns the API in both modes; normal down below stops it.
        pass

    def start(self):
        ports = self.config()
        self.created = True  # Also clean partial provisioning failures.
        self.call('up', '-d', '--wait', '--wait-timeout', '60', 'postgres', 'redis')
        tool_image = f'{self.project}-tooling'
        try:
            run(['docker', 'build', '--target', 'tooling', '-t', tool_image, '.'])
            run(['docker', 'run', '--rm', '--network', f'{self.project}_default',
                 '-e', f'ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=service_{self.mode};Username=service;Password=workflow_test_only',
                 tool_image, 'dotnet', 'ef', 'database', 'update', '--project', 'src/Service.Api'])
        finally:
            run(['docker', 'image', 'rm', tool_image])
        self.start_api(build=True)
        self.crud()
        self.item = json.loads(request(self.base, 'POST', '/items', 201,
                               {'name': f'certification-{RUN}-{self.mode}'})[0])
        migration = self.sql('SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId"')
        assert migration
        for cycle in range(2):
            self.stop_api()
            self.call('down')  # NORMAL stop: deliberately no -v.
            assert run(['docker', 'volume', 'ls', '-q', '--filter',
                        f'label=com.docker.compose.project={self.project}']).strip()
            self.call('up', '-d', '--wait', '--wait-timeout', '60', 'postgres', 'redis')
            self.start_api()
            assert self.sql('SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId"') == migration
            saved = json.loads(request(self.base, 'GET', '/items/' + self.item['id'])[0])
            assert saved['name'] == self.item['name']
            print(f'{self.mode.upper()} persistence cycle {cycle + 1}: passed', flush=True)

    def crud(self):
        base = self.base
        assert request(base, 'GET', '/health')[0] == 'Healthy'
        doc = json.loads(request(base, 'GET', '/swagger/v1/swagger.json')[0])
        assert set(doc['paths']) == {'/items', '/items/{itemId}', '/items/{itemId}/actions', '/actions', '/actions/{actionId}'}
        assert 'swagger-ui' in request(base, 'GET', '/swagger/index.html')[0]
        item, headers = request(base, 'POST', '/items', 201, {'name': f'crud-{RUN}'})
        item = json.loads(item)
        path = '/items/' + item['id']
        assert headers['Location'].endswith(path)
        # Exercise standalone Action creation/deletion as well as nested creation/cascade.
        action = json.loads(request(base, 'POST', '/actions', 201,
                            {'itemId': item['id'], 'name': 'standalone', 'type': 'create'})[0])
        action_path = '/actions/' + action['id']
        assert request(base, 'DELETE', action_path, 204)[0] == ''
        request(base, 'GET', action_path, 404)
        body, headers = request(base, 'POST', path + '/actions', 201, {'name': 'nested', 'type': 'create'})
        action = json.loads(body)
        action_path = '/actions/' + action['id']
        assert action['itemId'] == item['id'] and headers['Location'].endswith(action_path)
        for _ in range(2):
            assert json.loads(request(base, 'GET', path)[0])['name'] == item['name']
            assert json.loads(request(base, 'GET', action_path)[0])['itemId'] == item['id']
        assert self.redis('EXISTS', f'service-{self.mode}:items:{item["id"]}') == '1'
        assert self.redis('EXISTS', f'service-{self.mode}:actions:{action["id"]}') == '1'
        request(base, 'PUT', path, 200, {'name': 'updated', 'status': 'archived'})
        assert self.redis('EXISTS', f'service-{self.mode}:items:{item["id"]}') == '0'
        assert json.loads(request(base, 'GET', path)[0])['status'] == 'archived'
        request(base, 'PUT', action_path, 200, {'name': 'updated', 'type': 'update'})
        assert self.redis('EXISTS', f'service-{self.mode}:actions:{action["id"]}') == '0'
        assert json.loads(request(base, 'GET', action_path)[0])['type'] == 'update'
        assert any(row['id'] == action['id'] for row in json.loads(request(base, 'GET', '/actions')[0]))
        assert any(row['id'] == item['id'] for row in json.loads(request(base, 'GET', '/items')[0]))
        assert [row['id'] for row in json.loads(request(base, 'GET', path + '/actions')[0])] == [action['id']]
        assert request(base, 'DELETE', path, 204)[0] == ''
        request(base, 'GET', path, 404)
        request(base, 'GET', action_path, 404)
        request(base, 'GET', path + '/actions', 404)
        assert self.redis('EXISTS', f'service-{self.mode}:actions:{action["id"]}') == '0'
        assert self.sql('SELECT count(*) FROM "Items"') == '0'
        assert self.sql('SELECT count(*) FROM "Actions"') == '0'
        print(f'{self.mode.upper()} actual workflow, Swagger, CRUD, cache and cascade: passed', flush=True)

    def close(self):
        if not self.created:
            return
        try:
            if self.item:
                request(self.base, 'DELETE', '/items/' + self.item['id'], 204)
                assert self.sql('SELECT count(*) FROM "Items"') == '0'
                assert self.redis('DBSIZE') == '0'
        finally:
            self.stop_api()
            # Destructive reset is exercised ONLY against this run's unique owned project.
            self.call('down', '-v', '--remove-orphans', '--rmi', 'local')
            for kind in ('volume', 'network'):
                assert not run(['docker', kind, 'ls', '-q', '--filter',
                                f'label=com.docker.compose.project={self.project}']).strip()


def certify_script(name, mode, expected):
    before = inventory()
    states = container_states(before['containers'])
    output = run([f'./scripts/{name}.sh'], dict(ENV, SERVICE_WORKFLOW_CERTIFICATION=mode), expected, timeout=600)
    matches = re.findall(r'Workflow resources: (service-(?:test|smoke)-[a-z0-9-]+)', output)
    assert len(matches) == 1, 'Missing owned-resource checkpoint'
    project = matches[0]
    assert inventory() == before, f'Resources leaked by {name}/{mode}: {project}'
    assert container_states(before['containers']) == states, 'Unrelated container was restarted'
    if name == 'smoke':
        assert 'Docker smoke passed' in output
        assert not run(['docker', 'image', 'ls', '-q', f'service-api:{project}']).strip()
    if mode:
        assert 'Certification:' in output and ('postgres' in output and 'redis' in output)
        if name == 'smoke':
            api_logs = output.split(f'API logs ({project}-api):', 1)
            assert len(api_logs) == 2 and 'info:' in api_logs[1], 'API failure logs were not emitted'
    print(f'{name}.sh {mode or "normal"}: exit {expected}, owned resources removed, unrelated resources unchanged', flush=True)


def main():
    print(f'Workflow evidence: {LOGS}', flush=True)
    before = inventory()
    states = container_states(before['containers'])
    print('Existing LOCAL/DEV projects (preserved):', run(['docker', 'compose', 'ls', '--all', '--format', 'json']), flush=True)
    # Never stop existing listeners to make room for certification.
    for port in (5080, 55432, 56379, 18080, 25432, 26379):
        with socket.socket() as listener:
            # Ignore TIME_WAIT from a just-stopped stack, never an active listener.
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            listener.bind(('127.0.0.1', port))
    local, dev = Workflow('local'), Workflow('dev')
    try:
        test_config = json.loads(run(['docker', 'compose', '--env-file', '/dev/null', '-p', f'service-cert-test-{RUN}',
                                     '-f', 'docker/compose.test.yml', 'config', '--format', 'json']))
        assert set(test_config['services']) == {'postgres', 'redis'}
        assert all('container_name' not in service for service in test_config['services'].values())
        assert all(service['ports'][0]['host_ip'] == '127.0.0.1' for service in test_config['services'].values())
        assert all(service['tmpfs'] for service in test_config['services'].values())
        local.start()
        dev.start()
        for name in ('test', 'smoke'):
            certify_script(name, '', 0)
            for _ in range(2):
                certify_script(name, 'fail', 73)
            certify_script(name, 'term', 143)
        for workflow in (local, dev):
            assert json.loads(request(workflow.base, 'GET', '/items/' + workflow.item['id'])[0])['name'] == workflow.item['name']
            assert workflow.sql('SELECT current_database()') == f'service_{workflow.mode}'
        print('LOCAL/DEV sentinel rows survived all TEST/smoke runs.', flush=True)
    finally:
        try:
            dev.close()
        finally:
            local.close()
    assert inventory() == before
    assert container_states(before['containers']) == states
    print('Workflow certification passed; no task-owned resources remain.', flush=True)


if __name__ == '__main__':
    main()
