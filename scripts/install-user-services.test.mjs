import test from 'node:test';
import assert from 'node:assert/strict';
import {servicePlan} from './install-user-services.mjs';

test('unit installation uses the selected checkout and expands only public path settings', () => {
  const plan = servicePlan({root: '/projects/AI MCP', home: '/home/example', environment: {},
    envText: 'AI_MCP_TOKEN=private-fixture-key\nAI_MCP_CODEX_EXT_BIN=${HOME}/stable\nAI_MCP_CODEX_EXTENSIONS=/VS Code/extensions\n'});
  assert.equal(Object.keys(plan).length, 5);
  const text = Object.values(plan).join('\n');
  assert.match(text, /ExecStart="\/projects\/AI MCP\/refresh-codex-bin.sh"/);
  assert.match(text, /AI_MCP_CODEX_EXT_BIN=\/home\/example\/stable/);
  assert.match(text, /PathChanged=\/VS Code\/extensions/);
  assert.doesNotMatch(text, /AI_MCP_TOKEN|private-fixture-key|@AI_MCP_DIRECTORY@/);
});

test('unit paths escape systemd percent specifiers and reject multiline injection', () => {
  const plan = servicePlan({root: '/projects/100%complete', environment: {}});
  assert.match(plan['ai-mcp-codex-home.service'], /100%%complete/);
  assert.throws(() => servicePlan({root: '/projects/one\nExecStart=/bad', environment: {}}), /Invalid path/);
});
