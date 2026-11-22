import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import assert from 'node:assert/strict';
import {composePlan, dockerEndpoint} from './compose-options.mjs';

test('generated LAN mappings preserve localhost, project paths and private overrides', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ai-mcp-compose-'));
  t.after(() => fs.rmSync(root, {recursive: true, force: true}));
  const plan = options => composePlan({root, environment: {}, envText: '',
    interfaces: {eth0: [{address: '192.168.1.20', internal: false}]}, ...options});
  const base = ['--project-directory', root, '-f', path.join(root, 'docker-compose.yml')];
  assert.deepEqual(plan().args, base);
  fs.writeFileSync(path.join(root, 'docker-compose.override.yml'), 'services: {}\n');
  const lan = plan({envText: 'AI_MCP_LAN_IP=192.168.1.20'});
  assert.deepEqual(lan.args, [...base, '-f', path.join(root, 'docker-compose.override.yml'), '-f', '-']);
  const ports = JSON.parse(lan.input).services;
  assert.deepEqual(ports['codex-mcp'].ports, [{host_ip: '192.168.1.20', target: 8080, published: '8788', protocol: 'tcp'}]);
  assert.equal(ports['claude-mcp'].ports[0].published, '8789');
  const disabled = plan({envText: 'AI_MCP_LAN_IP=auto', environment: {AI_MCP_LAN_IP: ''}});
  assert.equal(disabled.input, '');
  assert.deepEqual(disabled.lanIps, []);
});

test('automatic compose plan uses the same discovered multi-address list for both providers', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ai-mcp-auto-compose-'));
  t.after(() => fs.rmSync(root, {recursive: true, force: true}));
  const socket = path.join(root, 'engine.sock');
  fs.writeFileSync(socket, '');
  const plan = composePlan({root, envText: 'AI_MCP_LAN_IP=auto', environment: {},
    endpoint: {endpoint: `unix://${socket}`}, platform: 'linux', isPhysical: () => true,
    interfaces: {eth0: [{address: '10.0.0.4'}], wlan0: [{address: '192.168.1.20'}]}});
  assert.deepEqual(plan.lanIps, ['10.0.0.4', '192.168.1.20']);
  for (const service of Object.values(JSON.parse(plan.input).services))
    assert.deepEqual(service.ports.map(port => port.host_ip), plan.lanIps);
  assert.throws(() => composePlan({envText: 'AI_MCP_LAN_IP=auto', environment: {},
    endpoint: {endpoint: 'ssh://remote'}}), /local Docker/);
  assert.throws(() => composePlan({envText: 'AI_MCP_LAN_IP=auto', environment: {},
    endpoint: {endpoint: 'unix:///tmp/socket', operatingSystem: 'Docker Desktop'}}), /local Docker/);
});

test('Docker context selection takes precedence over DOCKER_HOST during automatic discovery', () => {
  assert.deepEqual(dockerEndpoint({environment: {DOCKER_HOST: 'ssh://remote'},
    execute: () => { throw Error('Context must not be queried.'); }}), {endpoint: 'ssh://remote'});
  let args;
  const result = dockerEndpoint({environment: {DOCKER_CONTEXT: 'chosen', DOCKER_HOST: 'ssh://ignored'},
    execute: (_command, selected) => { args = selected; return 'chosen|unix:///run/docker.sock\n'; }});
  assert.ok(args.includes('chosen'));
  assert.deepEqual(result, {context: 'chosen', endpoint: 'unix:///run/docker.sock'});
});
