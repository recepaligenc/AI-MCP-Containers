import test from 'node:test';
import assert from 'node:assert/strict';
import {hostPaths, lanAddress} from './host-paths.mjs';

test('public paths use .env selections and HOME expansion without exporting credentials', () => {
  const paths = hostPaths({home: '/home/example', environment: {}, envText:
    'AI_MCP_TOKEN=private-fixture\nAI_MCP_PROJECT_ROOT=/projects\nAI_MCP_CODEX_EXT_BIN="${HOME}/selected stable"\n'});
  assert.equal(paths.codexBinary, '/home/example/selected stable');
  assert.equal(paths.projectRoot, '/projects');
  assert.equal(paths.claudeExtensions, '/home/example/.vscode/extensions');
  assert.doesNotMatch(JSON.stringify(paths), /private-fixture|AI_MCP_TOKEN/);
});

test('exported paths override .env; relative or multiline values are rejected', () => {
  assert.equal(hostPaths({home: '/home/example', envText: 'AI_MCP_CODEX_EXT_BIN=/file-value',
    environment: {AI_MCP_CODEX_EXT_BIN: '/exported'}}).codexBinary, '/exported');
  for (const value of ['relative', '/one\n/two'])
    assert.throws(() => hostPaths({envText: '', environment: {AI_MCP_CODEX_EXT_BIN: value}}), /absolute single-line/);
});

test('LAN selection reads only the public address and supports an explicit opt-out', () => {
  const envText = 'AI_MCP_TOKEN=private-fixture\nAI_MCP_LAN_IP=192.168.1.20\n';
  assert.equal(lanAddress({envText, environment: {}}), '192.168.1.20');
  assert.equal(lanAddress({envText, environment: {AI_MCP_LAN_IP: ''}}), '');
  assert.throws(() => lanAddress({envText, environment: {AI_MCP_LAN_IP: 'one\ntwo'}}), /single IPv4/);
});
