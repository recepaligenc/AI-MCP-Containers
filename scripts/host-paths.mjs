import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const pathKeys = new Set(['AI_MCP_PROJECT_ROOT', 'AI_MCP_CODEX_EXT_BIN',
  'AI_MCP_CODEX_EXTENSIONS', 'AI_MCP_CLAUDE_EXTENSIONS', 'AI_MCP_LAN_IP']);

// Parse public path settings only. Never source a private .env as shell code.
function publicValues(root, envText) {
  if (envText === undefined) {
    const file = path.join(root, '.env');
    envText = fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : '';
  }
  const values = {};
  for (const line of envText.split('\n')) {
    const match = /^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$/.exec(line);
    if (match && pathKeys.has(match[1])) values[match[1]] = match[2].replace(/^["']|["']$/g, '');
  }
  return values;
}

export function lanAddress({root = repository, envText, environment = process.env} = {}) {
  const value = environment.AI_MCP_LAN_IP ?? publicValues(root, envText).AI_MCP_LAN_IP ?? '';
  if (/[\r\n\0]/.test(value)) throw Error('The LAN address must be a single IPv4 address.');
  return value;
}

export function hostPaths({root = repository, home = os.homedir(), envText,
  environment = process.env} = {}) {
  const values = publicValues(root, envText);
  const selected = (key, fallback) => {
    const value = (environment[key] || values[key] || fallback)?.replaceAll('${HOME}', home);
    if (value !== undefined && (!path.isAbsolute(value) || /[\r\n\0]/.test(value)))
      throw Error('Host paths must be absolute single-line values.');
    return value;
  };
  return {projectRoot: selected('AI_MCP_PROJECT_ROOT'),
    codexBinary: selected('AI_MCP_CODEX_EXT_BIN', path.join(home, '.codex-ext-bin')),
    codexExtensions: selected('AI_MCP_CODEX_EXTENSIONS', path.join(home, '.vscode/extensions')),
    claudeExtensions: selected('AI_MCP_CLAUDE_EXTENSIONS', path.join(home, '.vscode/extensions')),
    codexHome: path.join(home, '.codex'), claudeSeed: path.join(home, '.claude')};
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    if (process.argv[2] !== '--codex') throw Error('Use --codex to resolve public Codex paths.');
    const paths = hostPaths();
    console.log(paths.codexExtensions + '\n' + paths.codexBinary);
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
