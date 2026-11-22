#!/usr/bin/node
// Resolve on every invocation: VS Code updates need no bridge image rebuild.
import fs from 'node:fs';
import path from 'node:path';
import {spawn} from 'node:child_process';
import {fileURLToPath} from 'node:url';

function canonical(file, directoryRequired = false) {
  const stat = fs.lstatSync(file);
  if (stat.isSymbolicLink() || fs.realpathSync(file) !== file ||
    (directoryRequired ? !stat.isDirectory() : !stat.isFile())) {
    throw Error('The VS Code Claude installation has a linked or invalid path.');
  }
  return stat;
}
function optionalMetadata(file, limit) {
  if (!fs.lstatSync(file, {throwIfNoEntry: false})) return null;
  if (canonical(file).size > limit) throw Error('VS Code installation metadata is oversized.');
  return fs.readFileSync(file, 'utf8');
}
function prereleaseFlag(metadata) {
  const value = metadata?.isPreReleaseVersion;
  if (value !== undefined && typeof value !== 'boolean') throw Error('Invalid VS Code pre-release metadata.');
  return value === true;
}
function vsixPrerelease(directory) {
  const xml = optionalMetadata(path.join(directory, '.vsixmanifest'), 1024 * 1024);
  if (xml === null) return false;
  // VS Code's VSIX property marks numeric pre-releases; semver suffixes alone
  // cannot distinguish them. Never expand XML entities or execute manifest data.
  const properties = [...xml.matchAll(/<Property\b([^>]*)\/?\s*>/g)].map(m => m[1]);
  const matches = properties.filter(p => /\bId\s*=\s*(["'])Microsoft\.VisualStudio\.Code\.PreRelease\1/.test(p));
  if (!matches.length) return false;
  const value = /\bValue\s*=\s*(["'])(true|false)\1/.exec(matches[0]);
  if (matches.length !== 1 || !value) throw Error('Invalid VSIX pre-release marker.');
  return value[2] === 'true';
}

export function selectClaude(root, {platform = process.platform, arch = process.arch} = {}) {
  if (platform !== 'linux' || !['x64', 'arm64'].includes(arch)) {
    throw Error('The bridge requires a Linux x64 or arm64 VS Code Claude executable.');
  }
  if (!root || !path.isAbsolute(root) || fs.realpathSync(root) !== root || !fs.statSync(root).isDirectory()) {
    throw Error('Mount the canonical VS Code extensions directory at AI_MCP_CLAUDE_EXTENSIONS.');
  }
  const obsoleteText = optionalMetadata(path.join(root, '.obsolete'), 4 * 1024 * 1024);
  const obsolete = obsoleteText === null ? {} : JSON.parse(obsoleteText);
  if (!obsolete || Array.isArray(obsolete) || typeof obsolete !== 'object' ||
    Object.values(obsolete).some(v => typeof v !== 'boolean')) throw Error('Invalid VS Code obsolete registry.');
  const registryText = optionalMetadata(path.join(root, 'extensions.json'), 4 * 1024 * 1024);
  const registry = registryText === null ? null : JSON.parse(registryText);
  if (registry !== null && !Array.isArray(registry)) throw Error('Invalid VS Code extension registry.');
  const pattern = new RegExp('^anthropic\\.claude-code-([0-9]+)\\.([0-9]+)\\.([0-9]+)-linux-' + arch + '$');
  const candidates = fs.readdirSync(root).flatMap(name => {
    const match = pattern.exec(name);
    if (!match || obsolete[name] === true) return [];
    const parts = match.slice(1).map(Number);
    if (!parts.every(Number.isSafeInteger)) return [];
    return [{name, parts, version: parts.join('.')}];
  }).sort((a, b) => b.parts[0] - a.parts[0] || b.parts[1] - a.parts[1] || b.parts[2] - a.parts[2]);
  for (const selected of candidates) {
  const directory = path.join(root, selected.name);
  try {
    const entries = registry?.filter(e => e?.relativeLocation === selected.name);
    if (entries?.length === 0) continue; // A removed or not-yet-registered folder is not an installed extension.
    if (entries && (entries.length !== 1 || entries[0].identifier?.id?.toLowerCase() !== 'anthropic.claude-code' ||
      entries[0].version !== selected.version)) throw Error('Extension registry identity differs.');
    canonical(directory, true);
    const manifestFile = path.join(directory, 'package.json');
    if (canonical(manifestFile).size > 1024 * 1024) throw Error('Extension manifest is oversized.');
    const manifest = JSON.parse(fs.readFileSync(manifestFile, 'utf8'));
    if (typeof manifest.publisher !== 'string' || manifest.publisher.toLowerCase() !== 'anthropic' ||
      manifest.name !== 'claude-code' || manifest.version !== selected.version) {
      throw Error('Extension identity or version differs.');
    }
    if (prereleaseFlag(entries?.[0]?.metadata) || prereleaseFlag(manifest.__metadata) || vsixPrerelease(directory)) continue;
    const executable = path.join(directory, 'resources/native-binary/claude');
    if (!(canonical(executable).mode & 0o111)) throw Error('Claude executable is not executable.');
    return {version: selected.version, executable};
  } catch (error) {
    throw Error('Newest VS Code Claude ' + selected.version + ' is incomplete or invalid. Finish or repair the extension update; refusing an older CLI.', {cause: error});
  }
  }
  throw Error('No compatible stable VS Code Claude extension is installed. No bundled CLI fallback is used.');
}

export async function runClaude(argv, {root = process.env.AI_MCP_CLAUDE_EXTENSIONS || '/vscode-extensions',
  environment = process.env, spawnImpl = spawn, signals = process} = {}) {
  const selected = selectClaude(root);
  return new Promise((resolve, reject) => {
    // No shell or argument rewriting. Preserve stdin, output, HOME and provider authentication.
    const child = spawnImpl(selected.executable, argv, {env: environment, stdio: 'inherit'});
    const handlers = new Map(['SIGTERM', 'SIGINT', 'SIGHUP'].map(signal => [signal, () => child.kill(signal)]));
    for (const [signal, handler] of handlers) signals.on(signal, handler);
    const cleanup = () => { for (const [signal, handler] of handlers) signals.removeListener(signal, handler); };
    child.once('error', error => { cleanup(); reject(error); });
    child.once('close', (code, signal) => { cleanup(); resolve({code, signal}); });
  });
}

if (process.argv[1] && fs.realpathSync(process.argv[1]) === fileURLToPath(import.meta.url)) {
  runClaude(process.argv.slice(2)).then(({code, signal}) => {
    if (signal) process.kill(process.pid, signal);
    else process.exitCode = code ?? 1;
  }).catch(error => { console.error('Claude launcher: ' + error.message); process.exitCode = 1; });
}
