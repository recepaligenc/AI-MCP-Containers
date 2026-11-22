import { test } from 'node:test';
import assert from 'node:assert/strict';
import { selectTests, changedPaths } from './affected-dotnet.mjs';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, renameSync, rmSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

test('security, validation, usage and sandbox changes select their direct tests', () => {
  for (const [path, name] of Object.entries({ 'ServiceSecurity.cs': 'ServiceSecurityTests', 'ChatModels.cs': 'ChatRequestValidatorTests', 'ProviderUsage.cs': 'NativeUsageTests', 'CodexSandboxProbe.cs': 'CodexSandboxProbeTests' })) {
    assert.ok(selectTests([path]).includes(`.${name}`));
    assert.equal(selectTests([`tests/${name}.cs`]), `FullyQualifiedName~AiMcpContainers.Tests.${name}`);
  }
});
test('test-only edits, deletions and rename paths retain precise selection', () => {
  assert.equal(selectTests(['tests/ResponsesApiTests.cs']), 'FullyQualifiedName~AiMcpContainers.Tests.ResponsesApiTests');
  assert.equal(selectTests(['tests/DeletedTests.cs'], () => false), '');
  const renamed = selectTests(['tests/OldTests.cs', 'tests/NewTests.cs']);
  assert.match(renamed, /OldTests/);
  assert.match(renamed, /NewTests/);
});
test('unknown production changes fail closed; documentation is explicitly skipped', () => {
  assert.throws(() => selectTests(['Unmapped.cs']), /No affected-test mapping/);
  assert.equal(selectTests(['README.md', 'docs/getting-started.md']), '');
});
test('selector, workflow and shared dependency changes select all affected consumers', () => {
  for (const path of ['scripts/affected-dotnet.mjs', 'scripts/affected-dotnet.test.mjs', '.github/workflows/dotnet-checks.yml', 'tests/AiMcpContainers.Tests.csproj', 'Directory.Build.props', 'global.json']) {
    const selected = selectTests([path]);
    for (const name of ['ServiceSecurityTests', 'ChatRequestValidatorTests', 'NativeUsageTests', 'CodexSandboxProbeTests', 'ProviderHealthTests', 'ChatEndpointTests']) assert.ok(selected.includes(`.${name}`), path + ' ' + name);
  }
});

test('push diff includes old and new rename paths; missing bases map tracked inputs', () => {
  const root = mkdtempSync(join(tmpdir(), 'affected-dotnet-'));
  const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  try {
    git('init');
    writeFileSync(join(root, 'ServiceSecurity.cs'), '// fixture');
    writeFileSync(join(root, 'README.md'), 'fixture');
    git('add', '.');
    git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-m', 'fixture');
    const before = git('rev-parse', 'HEAD');
    renameSync(join(root, 'ServiceSecurity.cs'), join(root, 'ChatModels.cs'));
    git('add', '-A');
    git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-m', 'rename');
    assert.deepEqual(changedPaths(before, 'HEAD', root).sort(), ['ChatModels.cs', 'ServiceSecurity.cs']);
    assert.deepEqual(changedPaths('0'.repeat(40), 'HEAD', root), ['ChatModels.cs']);
    assert.deepEqual(changedPaths('missing-base', 'HEAD', root), ['ChatModels.cs']);
    assert.ok(selectTests(changedPaths(before, 'HEAD', root)).includes('ServiceSecurityTests'));
    const outputPath = join(root, 'github-output');
    const envPath = join(root, 'github-environment');
    execFileSync(process.execPath, [fileURLToPath(new URL('./affected-dotnet.mjs', import.meta.url)), before, 'HEAD'],
      { cwd: root, env: { ...process.env, GITHUB_OUTPUT: outputPath, GITHUB_ENV: envPath } });
    assert.match(readFileSync(envPath, 'utf8'), /TEST_FILTER=.*ServiceSecurityTests/);
    assert.match(readFileSync(outputPath, 'utf8'), /selector_changed=false/);
    mkdirSync(join(root, 'tests'));
    writeFileSync(join(root, 'tests/RetiredTests.cs'), '// retired fixture');
    git('add', 'tests/RetiredTests.cs');
    git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-m', 'test fixture');
    const retiredBase = git('rev-parse', 'HEAD');
    git('rm', 'tests/RetiredTests.cs');
    git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-m', 'delete test');
    const message = execFileSync(process.execPath, [fileURLToPath(new URL('./affected-dotnet.mjs', import.meta.url)), retiredBase, 'HEAD'],
      { cwd: root, encoding: 'utf8', env: { ...process.env, GITHUB_OUTPUT: outputPath, GITHUB_ENV: envPath } });
    assert.match(message, /skipped: no executable tests/);
    assert.match(readFileSync(outputPath, 'utf8'), /skip=true/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
