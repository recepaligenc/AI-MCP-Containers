import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {validateProjectDirectory, desktopArgs, desktopCommand, checkDesktopConfiguration} from './desktop.mjs';

test('Desktop project validation handles Windows drives and macOS paths without host-to-container confusion', () => {
  for (const [api, home, project] of [
    [path.win32, 'C:\\Users\\Alice', 'C:\\Users\\Alice\\Projects\\My App'],
    [path.posix, '/Users/alice', '/Users/alice/Projects/My App'],
  ]) {
    const options = {pathApi: api, home, canonical: p => api.normalize(p), directory: () => true};
    assert.equal(validateProjectDirectory(project, options), api.normalize(project));
    for (const bad of [api.parse(home).root, home, api.dirname(home), 'relative', project + '\nother'])
      assert.throws(() => validateProjectDirectory(bad, options));
    assert.throws(() => validateProjectDirectory(project, {...options, canonical: () => home}), /home/);
  }
  assert.throws(() => validateProjectDirectory('C:relative', {pathApi: path.win32}), /absolute/);
});

test('Desktop paths are checked against the real filesystem and credential homes cannot be exposed', t => {
  const project = fs.mkdtempSync(path.join(os.tmpdir(), 'ai-mcp-desktop-'));
  t.after(() => fs.rmSync(project, {recursive: true, force: true}));
  assert.equal(validateProjectDirectory(project), fs.realpathSync(project));
  assert.throws(() => validateProjectDirectory(os.homedir()), /home/);
  assert.throws(() => validateProjectDirectory(path.join(project, 'missing')));
});

test('Desktop commands use explicit files and argv, preserve credentials and select provider login flows', () => {
  const root = path.join(os.tmpdir(), 'repo with spaces');
  assert.deepEqual(desktopArgs({root}), ['compose', '--project-directory', root, '--env-file',
    path.join(root, '.env.desktop'), '-f', path.join(root, 'docker-compose.desktop.yml')]);
  assert.deepEqual(desktopCommand('build'), ['build', 'codex-mcp']);
  assert.deepEqual(desktopCommand('down'), ['down']);
  assert.deepEqual(desktopCommand('login', 'codex').slice(-3), ['codex-mcp', 'login', '--device-auth']);
  assert.deepEqual(desktopCommand('login', 'claude').slice(-3), ['claude-mcp', 'auth', 'login']);
  assert.deepEqual(desktopCommand('status', 'codex').slice(-2), ['login', 'status']);
  assert.deepEqual(desktopCommand('status', 'claude').slice(-2), ['auth', 'status']);
  assert.throws(() => desktopCommand('login', 'codex; other'));
  assert.throws(() => desktopCommand('unknown'));
});

function configuration() {
  const make = provider => ({user: '1000:1000', read_only: true, cap_drop: ['ALL'],
    security_opt: ['no-new-privileges:true'], mem_limit: '2147483648', cpus: 2, pids_limit: 256,
    ports: [{host_ip: '127.0.0.1', target: 8080, published: provider === 'codex' ? '8788' : '8789'}],
    environment: {AI_MCP_TOKEN: 't'.repeat(32), AI_MCP_ALLOWED_ROOTS: '/workspace',
      AI_MCP_DEFAULT_CWD: '/workspace', AI_MCP_READ_ONLY_PROJECTS: 'true',
      AI_MCP_PROVIDER: provider, AI_MCP_PROVIDERS: provider,
      [provider === 'codex' ? 'AI_MCP_CODEX_REUSE_SESSION' : 'AI_MCP_CLAUDE_REUSE_SESSION']: 'false',
      [provider === 'codex' ? 'AI_MCP_CODEX_EPHEMERAL' : 'AI_MCP_CLAUDE_NO_SESSION_PERSISTENCE']: 'true'},
    volumes: [{type: 'bind', source: '/Users/alice/project', target: '/workspace', read_only: true,
      bind: {create_host_path: false}},
    {type: 'volume', source: `desktop-${provider}-home`, target: '/home/agent'}]});
  return {services: {'codex-mcp': make('codex'), 'claude-mcp': make('claude')}};
}
const macPaths = {pathApi: path.posix, home: '/Users/alice', canonical: p => p, directory: () => true};

test('Desktop preflight accepts the two isolated providers without disclosing credentials', () => {
  const c = configuration(), summary = checkDesktopConfiguration(c, macPaths);
  assert.equal(summary.serverProject, '/workspace');
  assert.equal(summary.hostProject, '/Users/alice/project');
  assert.equal(summary.services.length, 2);
  assert.ok(!JSON.stringify(summary).includes(c.services['codex-mcp'].environment.AI_MCP_TOKEN));
});

test('Desktop preflight accepts Compose normalization of a false create_host_path flag', () => {
  const c = configuration();
  for (const s of Object.values(c.services)) s.volumes[0].bind = {};
  assert.equal(checkDesktopConfiguration(c, macPaths).serverProject, '/workspace');
});

for (const [name, mutate] of [
  ['writable projects', s => {s.volumes[0].read_only = false;}],
  ['automatic bind creation', s => {s.volumes[0].bind.create_host_path = true;}],
  ['host credential bind', s => {s.volumes[1].type = 'bind';}],
  ['shared credential volume', s => {s.volumes[1].source = 'desktop-claude-home';}],
  ['extra mounts', s => {s.volumes.push({type: 'bind', source: '/', target: '/host'});}],
  ['root user', s => {s.user = '0:0';}],
  ['host namespaces', s => {s.pid = 'host';}],
  ['extra capabilities', s => {s.cap_add = ['SYS_ADMIN'];}],
  ['public listener', s => {s.ports[0].host_ip = '0.0.0.0';}],
  ['weak key', s => {s.environment.AI_MCP_TOKEN = 'short';}],
  ['writable root', s => {s.environment.AI_MCP_WRITE_ROOTS = '/workspace';}],
  ['provider reuse', s => {s.environment.AI_MCP_CODEX_REUSE_SESSION = 'true';}],
]) test('Desktop preflight rejects ' + name, () => {
  const c = configuration(); mutate(c.services['codex-mcp']);
  assert.throws(() => checkDesktopConfiguration(c, macPaths));
});
