import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';

const script = fileURLToPath(new URL('../refresh-codex-bin.sh', import.meta.url));
function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-update-'));
  t.after(() => fs.rmSync(root, {recursive: true, force: true}));
  const extensions = path.join(root, 'extensions'), stable = path.join(root, 'stable');
  fs.mkdirSync(extensions); fs.mkdirSync(stable);
  const install = (version, {arch = 'x64', executable = true, body = 'new', time = 1000} = {}) => {
    const bin = path.join(extensions, `openai.chatgpt-${version}-linux-${arch}`, 'bin', 'linux-x86_64');
    fs.mkdirSync(bin, {recursive: true});
    const binary = path.join(bin, 'codex');
    fs.writeFileSync(binary, body, {mode: executable ? 0o755 : 0o644});
    fs.utimesSync(binary, time, time);
    return bin;
  };
  const run = (...args) => spawnSync('bash', [script, ...args], {
    encoding: 'utf8', env: {...process.env, AI_MCP_CODEX_EXTENSIONS: extensions, AI_MCP_CODEX_EXT_BIN: stable,
      AI_MCP_PROJECT_ROOT: path.join(root, 'project')},
  });
  return {root, stable, install, run};
}

test('selects the highest numeric extension version even with an older timestamp', t => {
  const f = fixture(t);
  f.install('26.9.999', {time: 9000});
  f.install('26.10.1', {time: 8000});
  const newest = f.install('26.1007.21434', {time: 1000});
  const result = f.run('--check');
  assert.equal(result.status, 0, result.stderr);
  assert.ok(result.stdout.includes('newest ext bin : ' + newest));
  assert.deepEqual(fs.readdirSync(f.stable), []);
});

test('ignores nonexecutable binaries, other architectures and nonnumeric versions', t => {
  const f = fixture(t), selected = f.install('26.10.1');
  f.install('99.0.0', {executable: false});
  f.install('98.0.0', {arch: 'arm64'});
  f.install('97.0.0-preview');
  const result = f.run('--check');
  assert.equal(result.status, 0, result.stderr);
  assert.ok(result.stdout.includes('newest ext bin : ' + selected));
});

test('missing compatible binaries fail without changing the stable copy', t => {
  const f = fixture(t), result = f.run('--check');
  assert.equal(result.status, 1);
  assert.match(result.stderr, /no openai.chatgpt/);
  assert.deepEqual(fs.readdirSync(f.stable), []);
});

test('sync replaces different same-size bytes with identical timestamps without a restart', t => {
  const f = fixture(t), source = f.install('26.10.1', {body: 'new', time: 1000});
  const old = path.join(f.stable, 'codex');
  fs.writeFileSync(old, 'old', {mode: 0o755}); fs.utimesSync(old, 1000, 1000);
  fs.writeFileSync(path.join(source, 'resource'), 'current resource');
  fs.writeFileSync(path.join(f.stable, 'obsolete'), 'old resource');
  fs.writeFileSync(path.join(f.stable, '.ai-mcp-managed-codex'), 'ai-mcp-containers-managed-codex-v1\n');
  const result = f.run();
  assert.equal(result.status, 0, result.stderr);
  assert.equal(fs.readFileSync(old, 'utf8'), 'new');
  assert.equal(fs.readFileSync(path.join(f.stable, 'resource'), 'utf8'), 'current resource');
  assert.equal(fs.existsSync(path.join(f.stable, 'obsolete')), false);
  assert.doesNotMatch(result.stdout, /recreating/);
  const unchanged = f.run();
  assert.equal(unchanged.status, 0, unchanged.stderr);
  assert.match(unchanged.stdout, /already current/);
});
