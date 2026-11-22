import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {checkConfiguration, checkLanAddress} from './check-config.mjs';

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ai-mcp-config-'));
  t.after(() => fs.rmSync(root, {recursive: true, force: true}));
  const paths = {projectRoot: root, codexBinary: path.join(root, 'codex-bin'),
    codexHome: path.join(root, 'codex-home'), claudeSeed: path.join(root, 'claude-seed'),
    claudeExtensions: path.join(root, 'extensions')};
  for (const value of Object.values(paths)) fs.mkdirSync(value, {recursive: true});
  const common = {environment: {AI_MCP_TOKEN: 't'.repeat(32),
    AI_MCP_READ_ONLY_PROJECTS: 'true', AI_MCP_ALLOWED_ROOTS: root, AI_MCP_DEFAULT_CWD: root},
    read_only: true, security_opt: ['no-new-privileges:true'], cap_drop: ['ALL'],
    mem_limit: '2147483648', cpus: 2, pids_limit: 256, ports: [{host_ip: '127.0.0.1', target: 8080, published: '8788'}],
    volumes: [{type: 'bind', source: root, target: root, read_only: true}]};
  const config = {services: {'codex-mcp': structuredClone(common), 'claude-mcp': structuredClone(common)}};
  config.services['codex-mcp'].user = '1000:1000';
  Object.assign(config.services['codex-mcp'].environment,
    {AI_MCP_CODEX_REUSE_SESSION: 'false', AI_MCP_CODEX_EPHEMERAL: 'true', AI_MCP_CODEX_HOME: paths.codexHome});
  config.services['codex-mcp'].volumes.push(
    {type: 'bind', source: paths.codexBinary, target: '/opt/codex', read_only: true},
    {type: 'bind', source: paths.codexHome, target: paths.codexHome});
  config.services['claude-mcp'].cap_add = ['CHOWN', 'DAC_OVERRIDE', 'SETUID', 'SETGID'];
  config.services['claude-mcp'].ports[0].published = '8789';
  Object.assign(config.services['claude-mcp'].environment,
    {AI_MCP_CLAUDE_REUSE_SESSION: 'false', AI_MCP_CLAUDE_NO_SESSION_PERSISTENCE: 'true',
      AI_MCP_HOST_UID: '1000', AI_MCP_HOST_GID: '1000'});
  config.services['claude-mcp'].volumes.push(
    {type: 'bind', source: paths.claudeExtensions, target: '/vscode-extensions', read_only: true},
    {type: 'bind', source: paths.claudeSeed, target: '/seed', read_only: true},
    {type: 'volume', source: 'claude-home', target: '/claudehome'});
  return {config, paths};
}

test('a single selected read-only root is used by both services without exposing secrets in the result', t => {
  const {config, paths} = fixture(t), result = checkConfiguration(config, paths, {lanIp: ''});
  assert.equal(result.length, 2);
  assert.ok(result.every(s => s.projectReadOnly && s.defaultCwd === s.projectRoot));
  assert.ok(!JSON.stringify(result).includes(config.services['codex-mcp'].environment.AI_MCP_TOKEN));
});

for (const [name, mutate, message] of [
  ['writable project', s => {s.volumes[0].read_only = false;}, /read-only/],
  ['additional host path', s => {s.volumes.push({type: 'bind', source: os.homedir(), target: '/unexpected', read_only: true});}, /Unexpected volume/],
  ['empty key', s => {s.environment.AI_MCP_TOKEN = '';}, /AI_MCP_TOKEN/],
  ['public listener', s => {s.ports[0].host_ip = '0.0.0.0';}, /localhost/],
  ['project-specific default', s => {s.environment.AI_MCP_DEFAULT_CWD += '/other';}, /default cwd/],
  ['missing privilege guard', s => {s.security_opt = [];}, /no-new-privileges/],
  ['shared provider session', s => {s.environment.AI_MCP_CODEX_REUSE_SESSION = 'true';}, /independent/],
  ['persisted provider session', s => {s.environment.AI_MCP_CODEX_EPHEMERAL = 'false';}, /persistence/],
  ['host root disguised as a binary mount', s => {s.volumes[1].source = '/';}, /bind sources/],
  ['privileged service', s => {s.privileged = true;}, /Privileged/],
  ['host PID namespace', s => {s.pid = 'host';}, /namespaces/],
  ['host networking', s => {s.network_mode = 'host';}, /namespaces/],
  ['extra capabilities', s => {s.cap_add = ['SYS_ADMIN'];}, /capability/],
  ['root Codex owner', s => {s.user = '0:0';}, /non-root/],
  ['missing provider mount', s => {s.volumes.pop();}, /mounts are missing/],
]) {
  test('rejects ' + name + ' even in a local Compose override', t => {
    const {config, paths} = fixture(t); mutate(config.services['codex-mcp']);
    assert.throws(() => checkConfiguration(config, paths, {lanIp: ''}), message);
  });
}

const lanIp = '192.168.1.20';
const interfaces = {eth0: [{address: lanIp, internal: false}]};
test('LAN access adds a selected private host address while retaining localhost', t => {
  const {config, paths} = fixture(t);
  for (const service of Object.values(config.services))
    service.ports.push({...service.ports[0], host_ip: lanIp});
  const result = checkConfiguration(config, paths, {lanIp, interfaces});
  assert.ok(result.every(service => !service.localhostOnly && service.lanAddress === lanIp));
  assert.throws(() => checkConfiguration(config, paths, {lanIp: '', interfaces}), /localhost mapping/);
  config.services['codex-mcp'].ports.shift();
  assert.throws(() => checkConfiguration(config, paths, {lanIp, interfaces}), /localhost mapping/);
});

test('wildcard, public, nonlocal and Docker bridge addresses cannot be selected as LAN bindings', () => {
  for (const value of ['0.0.0.0', '203.0.113.5', '127.0.0.1', '::1', 'localhost'])
    assert.throws(() => checkLanAddress(value, interfaces), /private LAN/);
  assert.throws(() => checkLanAddress('192.168.1.99', interfaces), /host network interface/);
  assert.throws(() => checkLanAddress(lanIp, {'br-example': [{address: lanIp, internal: false}]}), /host network interface/);
});

test('a selected provider path still cannot expose the entire host', t => {
  const {config, paths} = fixture(t);
  paths.codexBinary = '/';
  config.services['codex-mcp'].volumes[1].source = '/';
  assert.throws(() => checkConfiguration(config, paths, {lanIp: ''}), /entire host or home/);
});
