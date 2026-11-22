import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import assert from 'node:assert/strict';
import {composeArguments} from './compose-options.mjs';

test('LAN compose mappings are opt-in and local resource overrides are preserved', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ai-mcp-compose-'));
  t.after(() => fs.rmSync(root, {recursive: true, force: true}));
  const args = options => composeArguments({root, environment: {}, envText: '', ...options});
  assert.deepEqual(args(), ['-f', path.join(root, 'docker-compose.yml')]);
  fs.writeFileSync(path.join(root, 'docker-compose.override.yml'), 'services: {}\n');
  const lan = args({envText: 'AI_MCP_LAN_IP=192.168.1.20'});
  assert.deepEqual(lan, ['-f', path.join(root, 'docker-compose.yml'), '-f', path.join(root, 'docker-compose.override.yml'),
    '-f', path.join(root, 'docker-compose.lan.yml')]);
  const disabled = args({envText: 'AI_MCP_LAN_IP=192.168.1.20', environment: {AI_MCP_LAN_IP: ''}});
  assert.ok(!disabled.some(value => value.endsWith('docker-compose.lan.yml')));
});
