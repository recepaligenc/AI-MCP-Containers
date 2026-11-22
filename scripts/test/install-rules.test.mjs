import test from 'node:test';
import assert from 'node:assert/strict';
import { promises as fs } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { installRules } from '../install-rules.mjs';

async function fixture(t) {
  const root = await fs.mkdtemp(join(tmpdir(), 'ai-mcp-rules-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  return root;
}
const wrap = text => `<!-- BEGIN AI-MCP COLLABORATION -->\n${text}\n<!-- END AI-MCP COLLABORATION -->\n`;
const options = { template: wrap('# Owner policy'), log() {} };

test('project installation preserves unrelated instructions and active client files', async t => {
  const root = await fixture(t);
  await fs.writeFile(join(root, 'AGENTS.md'), 'Existing owner instruction.\n');
  await fs.writeFile(join(root, 'AGENTS.override.md'), 'Priority instruction.\n');
  await fs.mkdir(join(root, '.claude'));
  await fs.writeFile(join(root, '.claude', 'CLAUDE.md'), 'Existing Claude rule.\n');
  const result = await installRules({ ...options, project: root });
  assert.equal(result.status, 'installed');
  assert.equal(await fs.readFile(result.canonical, 'utf8'), options.template);
  assert.match(await fs.readFile(join(root, 'AGENTS.md'), 'utf8'), /^Existing owner instruction\./);
  assert.match(await fs.readFile(join(root, 'AGENTS.override.md'), 'utf8'), /Priority instruction\.[\s\S]*collaboration\.md/);
  assert.match(await fs.readFile(join(root, '.claude', 'CLAUDE.md'), 'utf8'), /@\.\.\/\.ai-mcp\/collaboration\.md/);
  await assert.rejects(fs.access(join(root, 'CLAUDE.md')));
});

test('existing identical installation warns; non-TTY refuses without writes and Keep preserves timestamps', async t => {
  const root = await fixture(t);
  const first = await installRules({ ...options, project: root });
  const before = await Promise.all(first.changed.map(async path => [path, await fs.readFile(path, 'utf8'), (await fs.stat(path)).mtimeMs]));
  const logs = [];
  await assert.rejects(installRules({ ...options, project: root, log: value => logs.push(value) }), /choose --existing/);
  assert.match(logs[0], /Existing AI-MCP/);
  assert.equal((await installRules({ ...options, project: root, existing: 'keep' })).status, 'kept');
  assert.equal((await installRules({ ...options, project: root, interactive: true, choose: async () => 'keep' })).status, 'kept');
  for (const [path, text, mtime] of before) {
    assert.equal(await fs.readFile(path, 'utf8'), text);
    assert.equal((await fs.stat(path)).mtimeMs, mtime);
  }
  await assert.rejects(fs.access(join(root, '.ai-mcp', 'backups')));
});

test('explicit Override updates managed policy, preserves owner text and creates private backups', async t => {
  const root = await fixture(t);
  await fs.writeFile(join(root, 'AGENTS.md'), 'Owner instructions.\n');
  await installRules({ ...options, project: root });
  const legacyAgents = 'Owner instructions.\n' + wrap('Read and follow ".ai-mcp/collaboration.md" before planning or implementing technical work. This owner-controlled policy also governs delegated agents.');
  await fs.writeFile(join(root, 'AGENTS.md'), legacyAgents);
  const result = await installRules({ ...options, project: root, existing: 'override', template: wrap('# New owner policy') });
  assert.equal(await fs.readFile(result.canonical, 'utf8'), wrap('# New owner policy'));
  assert.match(await fs.readFile(join(root, 'AGENTS.md'), 'utf8'), /^Owner instructions\./);
  assert.match(await fs.readFile(join(root, 'AGENTS.md'), 'utf8'), /before any technical work, including documentation, code or configuration audits/);
  assert.equal((await fs.stat(result.backup)).mode & 0o777, 0o700);
  const index = JSON.parse(await fs.readFile(join(result.backup, 'index.json'), 'utf8'));
  assert.equal(index.length, 2);
  assert.equal(await fs.readFile(join(result.backup, index[0].file), 'utf8'), options.template);
  const backups = await Promise.all(index.map(item => fs.readFile(join(result.backup, item.file), 'utf8')));
  assert.ok(backups.includes(legacyAgents));
  assert.equal((await fs.stat(join(result.backup, index[0].file))).mode & 0o777, 0o600);
});

test('global installation has one canonical policy and respects custom config roots with spaces', async t => {
  const root = await fixture(t);
  const codexRoot = join(root, 'Codex Config');
  const claudeRoot = join(root, 'Claude Config');
  await fs.mkdir(codexRoot);
  await fs.writeFile(join(codexRoot, 'AGENTS.override.md'), 'Global priority.\n');
  const result = await installRules({ ...options, user: true, codexRoot, claudeRoot });
  assert.equal(result.canonical, join(claudeRoot, '.ai-mcp', 'collaboration.md'));
  assert.match(await fs.readFile(join(claudeRoot, 'CLAUDE.md'), 'utf8'), /@\.ai-mcp\/collaboration\.md/);
  assert.ok((await fs.readFile(join(codexRoot, 'AGENTS.override.md'), 'utf8')).includes(JSON.stringify(result.canonical)));
  await assert.rejects(fs.access(join(codexRoot, '.ai-mcp', 'collaboration.md')));
});

test('symlinked destinations and ambiguous managed blocks fail before writes', async t => {
  const root = await fixture(t);
  const outside = await fixture(t);
  await fs.symlink(outside, join(root, '.ai-mcp'));
  await assert.rejects(installRules({ ...options, project: root }), /symlink/);
  assert.deepEqual(await fs.readdir(outside), []);
  await fs.unlink(join(root, '.ai-mcp'));
  await fs.writeFile(join(root, 'AGENTS.md'), '<!-- BEGIN AI-MCP COLLABORATION -->\nbroken');
  await assert.rejects(installRules({ ...options, project: root }), /Ambiguous/);
  await assert.rejects(fs.access(join(root, '.ai-mcp')));
});


test('Override preserves surrounding canonical owner rules and rejects ambiguous canonical blocks', async t => {
  const root = await fixture(t);
  const first = await installRules({ ...options, project: root });
  await fs.writeFile(first.canonical, 'Owner preface.\n' + options.template + 'Owner appendix.\n');
  await installRules({ ...options, project: root, existing: 'override', template: wrap('# Revised policy') });
  assert.equal(await fs.readFile(first.canonical, 'utf8'), 'Owner preface.\n' + wrap('# Revised policy') + 'Owner appendix.\n');
  await fs.writeFile(first.canonical, '<!-- BEGIN AI-MCP COLLABORATION -->\nbroken');
  const agentsBefore = await fs.readFile(join(root, 'AGENTS.md'), 'utf8');
  await assert.rejects(installRules({ ...options, project: root, existing: 'override' }), /Ambiguous/);
  assert.equal(await fs.readFile(join(root, 'AGENTS.md'), 'utf8'), agentsBefore);
});

test('CLI installs the actual template and requires a non-interactive existing-setup choice', async t => {
  const root = await fixture(t);
  const script = fileURLToPath(new URL('../install-rules.mjs', import.meta.url));
  const run = extra => spawnSync(process.execPath, [script, '--project', root, ...extra], { encoding: 'utf8' });
  const initial = run([]);
  assert.equal(initial.status, 0, initial.stderr);
  const canonical = join(root, '.ai-mcp', 'collaboration.md');
  const before = await fs.readFile(canonical, 'utf8');
  const template = await fs.readFile(new URL('../../docs/templates/ai-mcp-collaboration.md', import.meta.url), 'utf8');
  assert.equal(before, template);
  assert.match(before, /Documentation, code and configuration audits MUST obtain complementary-provider/);
  assert.match(before, /Critique these audit findings and evidence\./);
  assert.equal(await fs.readFile(new URL('../../.ai-mcp/collaboration.md', import.meta.url), 'utf8'), template);
  assert.match(await fs.readFile(join(root, 'AGENTS.md'), 'utf8'), /Read and follow "\.ai-mcp\/collaboration\.md" before any technical work, including documentation, code or configuration audits/);
  assert.match(await fs.readFile(join(root, 'CLAUDE.md'), 'utf8'), /@\.ai-mcp\/collaboration\.md/);
  const mtime = (await fs.stat(canonical)).mtimeMs;
  const undecided = run([]);
  assert.equal(undecided.status, 1);
  assert.match(undecided.stdout, /Existing AI-MCP/);
  assert.match(undecided.stderr, /No files were changed/);
  const kept = run(['--existing', 'keep']);
  assert.equal(kept.status, 0, kept.stderr);
  assert.equal(await fs.readFile(canonical, 'utf8'), before);
  assert.equal((await fs.stat(canonical)).mtimeMs, mtime);
});

test('Update creates missing structure and leaves matching files, timestamps and inodes unchanged', async t => {
  const root = await fixture(t);
  const first = await installRules({ ...options, project: root, existing: 'update' });
  assert.equal(first.changed.length, 3);
  const agents = join(root, 'AGENTS.md');
  const legacyPointer = wrap('Read and follow ".ai-mcp/collaboration.md" before planning or implementing technical work. This owner-controlled policy also governs delegated agents.');
  await fs.writeFile(agents, legacyPointer);
  const before = await Promise.all(first.changed.map(async path => [path,
    await fs.readFile(path, 'utf8'), await fs.stat(path)]));
  const second = await installRules({ ...options, project: root, existing: 'update', template: wrap('# Different template') });
  assert.equal(second.policyPreserved, true);
  assert.deepEqual(second.changed, []);
  assert.equal(second.backup, undefined);
  for (const [path, content, stat] of before) {
    assert.equal(await fs.readFile(path, 'utf8'), content);
    const after = await fs.stat(path);
    assert.equal(after.mtimeMs, stat.mtimeMs);
    assert.equal(after.ino, stat.ino);
  }
  assert.equal(await fs.readFile(agents, 'utf8'), legacyPointer);
});

test('actual template user installation distributes audit policy and audit-aware client references', async t => {
  const root = await fixture(t);
  const codexRoot = join(root, 'Codex Config');
  const claudeRoot = join(root, 'Claude Config');
  const result = await installRules({ user: true, codexRoot, claudeRoot, log() {} });
  const template = await fs.readFile(new URL('../../docs/templates/ai-mcp-collaboration.md', import.meta.url), 'utf8');
  assert.equal(await fs.readFile(result.canonical, 'utf8'), template);
  const agents = await fs.readFile(join(codexRoot, 'AGENTS.md'), 'utf8');
  assert.ok(agents.includes(`Read and follow ${JSON.stringify(result.canonical)} before any technical work, including documentation, code or configuration audits`));
  assert.match(await fs.readFile(join(claudeRoot, 'CLAUDE.md'), 'utf8'), /@\.ai-mcp\/collaboration\.md/);
});

test('Update repairs missing references while preserving custom policy and backing up appended instructions', async t => {
  const root = await fixture(t);
  await fs.mkdir(join(root, '.ai-mcp'));
  const canonical = join(root, '.ai-mcp', 'collaboration.md');
  await fs.writeFile(canonical, '# Custom owner policy\n');
  const stat = await fs.stat(canonical);
  await fs.writeFile(join(root, 'AGENTS.md'), 'Owner instruction.\n');
  await fs.writeFile(join(root, 'AGENTS.override.md'), 'Priority owner instruction.\n');
  await fs.mkdir(join(root, '.claude'));
  await fs.writeFile(join(root, '.claude', 'CLAUDE.md'), 'Claude owner instruction.\n');
  const result = await installRules({ ...options, project: root, existing: 'update' });
  assert.equal(result.policyPreserved, true);
  assert.equal(await fs.readFile(canonical, 'utf8'), '# Custom owner policy\n');
  assert.equal((await fs.stat(canonical)).mtimeMs, stat.mtimeMs);
  assert.equal((await fs.stat(canonical)).ino, stat.ino);
  assert.match(await fs.readFile(join(root, 'AGENTS.override.md'), 'utf8'), /^Priority owner instruction\.[\s\S]*collaboration\.md/);
  assert.match(await fs.readFile(join(root, '.claude', 'CLAUDE.md'), 'utf8'), /@\.\.\/\.ai-mcp\/collaboration\.md/);
  const index = JSON.parse(await fs.readFile(join(result.backup, 'index.json'), 'utf8'));
  assert.equal(index.length, 3);
  assert.equal((await fs.stat(result.backup)).mode & 0o777, 0o700);
  for (const item of index) assert.equal((await fs.stat(join(result.backup, item.file))).mode & 0o777, 0o600);
});

test('Update rejects conflicting or malformed managed references before any writes', async t => {
  for (const body of ['Read and follow "/another/policy.md" before work.', '@another-policy.md',
    '```text\n@.ai-mcp/collaboration.md\n```',
    '<!-- BEGIN AI-MCP COLLABORATION -->\nbroken']) {
    const root = await fixture(t);
    const path = join(root, body.includes('@') ? 'CLAUDE.md' : 'AGENTS.md');
    const original = body.includes('<!--') ? body : wrap(body);
    await fs.writeFile(path, original);
    const before = await fs.stat(path);
    await assert.rejects(installRules({ ...options, project: root, existing: 'update' }), /conflict|Ambiguous/);
    assert.equal(await fs.readFile(path, 'utf8'), original);
    assert.equal((await fs.stat(path)).mtimeMs, before.mtimeMs);
    assert.equal((await fs.stat(path)).ino, before.ino);
    await assert.rejects(fs.access(join(root, '.ai-mcp')));
  }
});

test('CLI Update preserves custom policy and adds missing references without prompting', async t => {
  const root = await fixture(t);
  // Project scope exercises the same CLI choice without repurposing user HOME variables.
  const script = fileURLToPath(new URL('../install-rules.mjs', import.meta.url));
  await fs.mkdir(join(root, '.ai-mcp'));
  await fs.writeFile(join(root, '.ai-mcp', 'collaboration.md'), '# Existing custom policy\n');
  const result = spawnSync(process.execPath, [script, '--project', root, '--existing', 'update'], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /Existing canonical policy preserved byte-for-byte/);
  assert.equal(await fs.readFile(join(root, '.ai-mcp', 'collaboration.md'), 'utf8'), '# Existing custom policy\n');
  assert.match(await fs.readFile(join(root, 'CLAUDE.md'), 'utf8'), /@\.ai-mcp\/collaboration\.md/);
});
