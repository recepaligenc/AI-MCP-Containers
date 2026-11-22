#!/usr/bin/env node
// Install one owner-controlled policy without replacing unrelated client instructions.
import { promises as fs, constants } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { homedir } from 'node:os';
import { randomUUID } from 'node:crypto';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createInterface } from 'node:readline/promises';

const BEGIN = '<!-- BEGIN AI-MCP COLLABORATION -->';
const END = '<!-- END AI-MCP COLLABORATION -->';
const TEMPLATE = new URL('../docs/templates/ai-mcp-collaboration.md', import.meta.url);

async function snapshot(path) {
  try {
    const stat = await fs.lstat(path);
    if (!stat.isFile() || stat.isSymbolicLink()) throw new Error(`Refusing non-regular file: ${path}`);
    return { text: await fs.readFile(path, 'utf8'), mode: stat.mode & 0o777 };
  } catch (error) {
    if (error.code === 'ENOENT') return null;
    throw error;
  }
}

async function guard(root, target) {
  const rel = relative(root, target);
  if (rel === '..' || rel.startsWith(`..${sep}`) || isAbsolute(rel)) throw new Error(`Path escapes installation root: ${target}`);
  // Reject symlinks in every existing descendant, including instruction directories.
  let current = root;
  for (const part of rel.split(sep).filter(Boolean)) {
    current = join(current, part);
    try {
      if ((await fs.lstat(current)).isSymbolicLink()) throw new Error(`Refusing symlink: ${current}`);
    } catch (error) { if (error.code !== 'ENOENT') throw error; }
  }
}

function pointer(text, body, path) {
  const begins = text.split(BEGIN).length - 1;
  const ends = text.split(END).length - 1;
  if (begins !== ends || begins > 1 || (begins && text.indexOf(END) < text.indexOf(BEGIN)))
    throw new Error(`Ambiguous managed markers: ${path}`);
  const block = `${BEGIN}\n${body}\n${END}`;
  if (begins) return text.slice(0, text.indexOf(BEGIN)) + block + text.slice(text.indexOf(END) + END.length);
  return text + (text && !text.endsWith('\n') ? '\n' : '') + (text ? '\n' : '') + block + '\n';
}

// Exported options let tests use temporary roots without changing HOME or client ENV.
export async function installRules({ project, user = false, codexRoot, claudeRoot,
  existing, interactive = false, choose, log = console.log,
  template = null } = {}) {
  if (Boolean(project) === Boolean(user)) throw new Error('Select exactly one scope: --project PATH or --user.');
  if (existing !== undefined && !['keep', 'update', 'override'].includes(existing)) throw new Error('--existing must be keep, update or override.');
  if (project && !isAbsolute(project)) throw new Error('--project must be an absolute path.');
  const policy = template ?? await fs.readFile(TEMPLATE, 'utf8');
  let root;
  let codex;
  let claude;
  if (user) {
    codex = resolve(codexRoot ?? process.env.CODEX_HOME ?? join(homedir(), '.codex'));
    claude = resolve(claudeRoot ?? process.env.CLAUDE_CONFIG_DIR ?? join(homedir(), '.claude'));
    root = claude;
    // User roots may not exist yet. Resolve the nearest existing ancestor first.
    for (const destination of [codex, claude]) {
      let ancestor = destination;
      while (true) {
        try {
          const stat = await fs.lstat(ancestor);
          if (!stat.isDirectory() || stat.isSymbolicLink()) throw new Error(`Refusing unsafe config root: ${ancestor}`);
          await guard(ancestor, destination);
          break;
        } catch (error) {
          if (error.code !== 'ENOENT') throw error;
          ancestor = dirname(ancestor);
        }
      }
    }
  } else {
    root = await fs.realpath(project);
    if (!(await fs.stat(root)).isDirectory()) throw new Error('Project root must be a directory.');
    codex = root;
    claude = root;
  }
  const canonical = join(root, '.ai-mcp', 'collaboration.md');
  await guard(root, canonical);
  const canonicalBefore = await snapshot(canonical);
  pointer(policy, '', 'collaboration template'); // Validate the template's marker boundaries.
  if (!policy.includes(BEGIN)) throw new Error('Collaboration template has no managed policy block.');
  const policyBody = policy.slice(policy.indexOf(BEGIN) + BEGIN.length, policy.indexOf(END)).trim();
  const candidates = [{ path: canonical, root, before: canonicalBefore,
    text: canonicalBefore ? pointer(canonicalBefore.text, policyBody, canonical) : policy }];
  const instructionTargets = [{ path: join(codex, 'AGENTS.md'), kind: 'codex', root: codex }];
  const override = join(codex, 'AGENTS.override.md');
  await guard(codex, override);
  if ((await snapshot(override))?.text.trim()) instructionTargets.push({ path: override, kind: 'codex', root: codex });
  const claudeFiles = user ? [join(claude, 'CLAUDE.md')] : [join(root, 'CLAUDE.md'), join(root, '.claude', 'CLAUDE.md')];
  const existingClaude = [];
  for (const path of claudeFiles) {
    await guard(claude, path);
    if (await snapshot(path)) existingClaude.push(path);
  }
  for (const path of existingClaude.length ? existingClaude : [claudeFiles[0]])
    instructionTargets.push({ path, kind: 'claude', root: claude });
  for (const item of instructionTargets) {
    await guard(item.root, item.path);
    const before = await snapshot(item.path);
    const policyPath = user ? canonical : '.ai-mcp/collaboration.md';
    const body = item.kind === 'codex'
      ? `Read and follow ${JSON.stringify(policyPath)} before planning or implementing technical work. This owner-controlled policy also governs delegated agents.`
      : `@${relative(dirname(item.path), canonical).split(sep).join('/').replace(/\s/g, '\\$&')}`;
    candidates.push({ ...item, body, text: pointer(before?.text ?? '', body, item.path), before });
  }
  const installed = candidates[0].before !== null || candidates.slice(1).some(item => item.before?.text.includes(BEGIN));
  let decision = existing;
  if (installed) {
    log(`Existing AI-MCP rules found at ${canonical}. Choose Keep, Update or Override (default: Keep).`);
    if (!decision && interactive && choose) decision = await choose();
    if (!decision) throw new Error('Existing installation: choose --existing keep, --existing update or --existing override. No files were changed.');
    if (decision === 'keep') { log('Keep selected. No files were changed.'); return { status: 'kept', canonical, changed: [] }; }
    if (!['update', 'override'].includes(decision)) throw new Error('Expected Keep, Update or Override. No files were changed.');
  }
  const policyPreserved = decision === 'update' && canonicalBefore !== null;
  if (decision === 'update') {
    if (canonicalBefore) candidates[0].text = canonicalBefore.text;
    for (const item of candidates.slice(1)) {
      if (!item.before?.text.includes(BEGIN)) continue;
      const currentBody = item.before.text.slice(item.before.text.indexOf(BEGIN) + BEGIN.length,
        item.before.text.indexOf(END)).trim();
      const matching = item.kind === 'claude'
        ? currentBody === item.body
        : [JSON.stringify(user ? canonical : '.ai-mcp/collaboration.md'),
          '`' + (user ? canonical : '.ai-mcp/collaboration.md') + '`',
          user ? canonical : '.ai-mcp/collaboration.md'].some(path => currentBody.startsWith(`Read and follow ${path} `));
      if (!matching) throw new Error(`Update conflict in managed reference: ${item.path}. No files were changed.`);
      item.text = item.before.text;
    }
  }
  const changes = candidates.filter(item => item.before?.text !== item.text);
  // Validate every snapshot before making any directory, backup or instruction change.
  for (const item of candidates) {
    await guard(item.root, item.path);
    if (JSON.stringify(await snapshot(item.path)) !== JSON.stringify(item.before)) throw new Error(`File changed during installation: ${item.path}`);
  }
  let backup;
  if (changes.some(item => item.before)) {
    backup = join(root, '.ai-mcp', 'backups', `${Date.now()}-${randomUUID()}`);
    await guard(root, backup);
    await fs.mkdir(backup, { recursive: true, mode: 0o700 });
    const ignorePath = join(dirname(backup), '.gitignore');
    await guard(root, ignorePath);
    try { await fs.writeFile(ignorePath, '*\n', { flag: 'wx', mode: 0o600 }); }
    catch (error) { if (error.code !== 'EEXIST') throw error; }
    const index = [];
    for (const [i, item] of changes.entries()) if (item.before) {
      await fs.writeFile(join(backup, `${i}.txt`), item.before.text, { flag: 'wx', mode: 0o600 });
      index.push({ file: `${i}.txt`, original: item.path });
    }
    await fs.writeFile(join(backup, 'index.json'), JSON.stringify(index, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
  }
  const written = [];
  try {
    for (const item of changes) {
      await guard(item.root, item.path);
      if (JSON.stringify(await snapshot(item.path)) !== JSON.stringify(item.before)) throw new Error(`File changed during installation: ${item.path}`);
      await fs.mkdir(dirname(item.path), { recursive: true, mode: 0o700 });
      const temp = join(dirname(item.path), `.ai-mcp-${randomUUID()}.tmp`);
      try {
        await fs.writeFile(temp, item.text, { flag: 'wx', mode: item.before?.mode ?? 0o600 });
        await fs.rename(temp, item.path);
      } finally { await fs.rm(temp, { force: true }); }
      written.push(item.path);
      if (item.path === canonical) await fs.access(canonical, constants.R_OK);
    }
    await fs.access(canonical, constants.R_OK);
  } catch (error) {
    throw new Error(`${error.message}. Installation may be partial (${written.length} files written). Backup: ${backup ?? 'none'}.`);
  }
  log(`AI-MCP rules installed: ${canonical}`);
  if (policyPreserved) log('Existing canonical policy preserved byte-for-byte. Missing references were added; latest-template parity is not asserted.');
  if (backup) log(`Previous changed files backed up privately: ${backup}`);
  if (user) log('Both native clients must be able to read the shared policy in the Claude config root. This check cannot prove future sandbox or container access; use project scope where necessary.');
  log('Start a new client session so its instruction files are loaded. MCP registration is a separate setup step.');
  return { status: 'installed', canonical, changed: written, backup, policyPreserved };
}

export async function main(args = process.argv.slice(2)) {
  const options = {};
  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--project') options.project = args[++i];
    else if (args[i] === '--user') options.user = true;
    else if (args[i] === '--existing') options.existing = args[++i];
    else if (args[i] === '--help') {
      console.log(`Usage: node scripts/install-rules.mjs --project /absolute/project | --user [--existing keep|update|override]
Install permanent collaboration rules for both Codex and Claude. No manual Markdown copying.
Existing AI-MCP setup always prompts Keep / Update / Override; Keep is the default and makes no changes.
Non-interactive sessions must explicitly pass --existing keep|update|override when setup exists.
Update adds missing files/references and preserves existing canonical policy bytes; conflicts stop before writes.
Update does not merge new rule clauses into a customized existing policy.
An AI may pass --existing override only after the owner explicitly chooses Override.
Override backs up changed existing files and preserves unrelated instruction text.
User scope respects CODEX_HOME and CLAUDE_CONFIG_DIR. Both clients need access to the shared policy.
This command does not register MCP servers or make writable rules technically immutable.`);
      return;
    } else throw new Error(`Unknown argument: ${args[i]}`);
  }
  const interactive = Boolean(process.stdin.isTTY && process.stdout.isTTY);
  await installRules({ ...options, interactive, choose: async () => {
    const rl = createInterface({ input: process.stdin, output: process.stdout });
    try {
      const answer = (await rl.question('Keep, Update or Override? [Keep]: ')).trim().toLowerCase();
      if (!answer || answer === 'keep' || answer === 'k') return 'keep';
      if (answer === 'update' || answer === 'u') return 'update';
      if (answer === 'override' || answer === 'o') return 'override';
      throw new Error('Expected Keep, Update or Override. No files were changed.');
    } finally { rl.close(); }
  } });
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href)
  main().catch(error => { console.error(error.message); process.exitCode = 1; });
