import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {execFileSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {hostPaths} from './host-paths.mjs';

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const unitNames = ['ai-mcp-codex-refresh.service', 'ai-mcp-codex-refresh.path',
  'ai-mcp-codex-refresh.timer', 'ai-mcp-codex-home.service', 'ai-mcp-codex-home.timer'];
const escapeValue = value => {
  if (/[\r\n\0]/.test(value)) throw Error('Invalid path in user-service configuration.');
  return value.replaceAll('\\', '\\\\').replaceAll('"', '\\"').replaceAll('%', '%%');
};

export function servicePlan({root = repository, home = os.homedir(), envText = '', environment = process.env} = {}) {
  const paths = hostPaths({root, home, envText, environment});
  const binary = paths.codexBinary, extensions = paths.codexExtensions;
  if (![root, binary, extensions].every(path.isAbsolute)) throw Error('User-service paths must be absolute.');
  return Object.fromEntries(unitNames.map(name => {
    let text = fs.readFileSync(path.join(repository, 'systemd', name), 'utf8')
      .replaceAll('@AI_MCP_DIRECTORY@', escapeValue(root));
    if (name === 'ai-mcp-codex-refresh.service')
      text = text.replace('[Service]', '[Service]\nEnvironment="AI_MCP_CODEX_EXT_BIN=' + escapeValue(binary) +
        '"\nEnvironment="AI_MCP_CODEX_EXTENSIONS=' + escapeValue(extensions) + '"');
    if (name === 'ai-mcp-codex-refresh.path')
      text = text.replace('PathChanged=%h/.vscode/extensions', 'PathChanged=' + escapeValue(extensions));
    return [name, text];
  }));
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const envFile = path.join(repository, '.env');
    const plan = servicePlan({envText: fs.existsSync(envFile) ? fs.readFileSync(envFile, 'utf8') : ''});
    if (!process.argv.includes('--install')) {
      console.log(JSON.stringify({installationDirectory: repository, units: Object.keys(plan),
        nextStep: 'Use --install to update and enable the user units.'}, null, 2));
    } else {
      const directory = path.join(os.homedir(), '.config/systemd/user');
      fs.mkdirSync(directory, {recursive: true, mode: 0o700});
      for (const [name, text] of Object.entries(plan)) fs.writeFileSync(path.join(directory, name), text, {mode: 0o644});
      try { execFileSync('systemctl', ['--user', 'disable', '--now', 'codex-ext-bin.path', 'codex-ext-bin.timer',
        'codex-bridge-home.timer'], {stdio: 'pipe'}); } catch { /* Legacy units may not exist. */ }
      execFileSync('systemctl', ['--user', 'daemon-reload'], {stdio: 'inherit'});
      execFileSync('systemctl', ['--user', 'enable', '--now', 'ai-mcp-codex-refresh.path',
        'ai-mcp-codex-refresh.timer', 'ai-mcp-codex-home.timer'], {stdio: 'inherit'});
      console.log('AI-MCP-Containers user services installed. No provider was restarted.');
    }
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
