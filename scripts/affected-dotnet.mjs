#!/usr/bin/env node
import { execFileSync } from 'node:child_process';
import { appendFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const runners = ['RunnerProcessTests', 'CodexRunnerSessionTests', 'ClaudeRunnerSessionTests', 'ClaudeRunnerAuthReseedTests', 'ClaudeWeeklyQuotaTests', 'CodexCapacityTests'];
const endpoints = ['ChatEndpointTests'];
const mappings = {
  'CodexRunner.cs': [...runners, ...endpoints, 'CodexConfigModelTests', 'NativeUsageTests'],
  'ClaudeRunner.cs': [...runners, ...endpoints, 'ClaudeRunnerParseTests', 'NativeUsageTests'],
  'ProviderProcess.cs': runners,
  'ProviderHealth.cs': ['ProviderHealthTests', 'ServiceSecurityTests', ...endpoints],
  'ServiceSecurity.cs': ['ServiceSecurityTests', 'ChatRequestValidatorTests', ...runners, ...endpoints],
  'ChatModels.cs': ['ChatRequestValidatorTests', ...endpoints, 'ClaudeWeeklyQuotaTests'],
  'ProviderUsage.cs': ['NativeUsageTests', 'ResponsesApiTests', ...runners, ...endpoints],
  'CodexSandboxProbe.cs': ['CodexSandboxProbeTests', 'ChatRequestValidatorTests', ...endpoints],
  'ResponsesApi.cs': ['ResponsesApiTests', ...endpoints],
  'Program.cs': [...endpoints, 'ClaudeWeeklyQuotaTests', 'CodexCapacityTests'],
};
const all = [...new Set(Object.values(mappings).flat())];
const globalInput = p => /(^|\/)([^/]+\.csproj|Directory\.[^/]+|[Nn]u[Gg]et\.Config|nuget\.config|packages\.lock\.json|global\.json)$/.test(p)
  || ['appsettings.json', '.github/workflows/dotnet-checks.yml', 'scripts/affected-dotnet.mjs', 'scripts/affected-dotnet.test.mjs'].includes(p);
const ignored = p => /\.md$/.test(p) || p.startsWith('docs/') || p === 'AGENTS.md' || p === 'CLAUDE.md';

export function selectTests(paths, exists = () => true) {
  const selected = new Set();
  for (const p of paths) {
    let classes;
    if (globalInput(p)) classes = all;
    else if (mappings[p]) classes = mappings[p];
    else if (/^tests\/[A-Za-z0-9_]+Tests\.cs$/.test(p)) {
      // A deleted test has no executable cases. Renamed tests are selected by
      // their new name; deleted production sources still use their old mapping.
      if (!exists(p)) continue;
      classes = [p.slice(6, -3)];
    }
    else if (ignored(p)) continue;
    else throw new Error(`No affected-test mapping for ${p}; add an explicit mapping before merging.`);
    for (const name of classes) selected.add(`FullyQualifiedName~AiMcpContainers.Tests.${name}`);
  }
  return [...selected].sort().join('|');
}

// --no-renames includes both the deleted old name and the new name.
export function changedPaths(before, after, cwd) {
  if (before && !/^0+$/.test(before)) {
    try {
      return execFileSync('git', ['diff', '--name-only', '--no-renames', '-z', before, after], { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).split('\0').filter(Boolean);
    } catch { /* new/force-pushed branch: conservatively map all tracked relevant inputs */ }
  }
  return execFileSync('git', ['ls-files', '-z'], { cwd, encoding: 'utf8' }).split('\0').filter(p =>
    p && (/\.cs$/.test(p) || globalInput(p)));
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  // The workflow only passes .NET-related inputs, but mixed pushes can include
  // unrelated files too. Keep this list aligned with its trigger paths.
  const relevant = p => /\.cs$/.test(p) || globalInput(p);
  const paths = changedPaths(process.argv[2], process.argv[3] || 'HEAD');
  const filter = selectTests(paths.filter(relevant), existsSync);
  if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT,
    `selector_changed=${paths.some(p => ['scripts/affected-dotnet.mjs', 'scripts/affected-dotnet.test.mjs', '.github/workflows/dotnet-checks.yml'].includes(p))}\n`);
  if (!filter) {
    console.log('skipped: no executable tests selected (deleted tests or unrelated inputs)');
    if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, 'skip=true\n');
  } else if (process.env.GITHUB_ENV) appendFileSync(process.env.GITHUB_ENV, `TEST_FILTER=${filter}\n`);
  else console.log(filter);
}
