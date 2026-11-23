#!/usr/bin/env node
// Docker Desktop entry point: no Bash, host binaries, host credentials or UID lookup.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {execFileSync, spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

export function validateProjectDirectory(value, {pathApi = path, home = os.homedir(),
  canonical = fs.realpathSync, directory = p => fs.statSync(p).isDirectory()} = {}) {
  if (typeof value !== 'string' || !pathApi.isAbsolute(value) || /[\r\n\0]/.test(value)
      || (pathApi === path.win32 && !/^[a-z]:[\\/]/i.test(value)))
    throw Error('Select an absolute project directory (use C:/... on Windows).');
  const root = canonical(value), userHome = canonical(home);
  const relativeHome = pathApi.relative(root, userHome);
  if (!directory(root) || root === pathApi.parse(root).root || relativeHome === '' ||
      (relativeHome !== '..' && !relativeHome.startsWith('..' + pathApi.sep) && !pathApi.isAbsolute(relativeHome)))
    throw Error('Select a project directory, not a filesystem root, home or ancestor of home.');
  return root;
}

export function desktopArgs({root = repository, envFile = path.join(root, '.env.desktop')} = {}) {
  return ['compose', '--project-directory', root, '--env-file', envFile,
    '-f', path.join(root, 'docker-compose.desktop.yml')];
}

export function desktopCommand(action, provider) {
  switch (action) {
    case 'check': return [];
    case 'build': return ['build', 'codex-mcp']; // Both services use the same image.
    case 'up': return ['up', '-d', '--no-build', '--pull', 'never'];
    case 'down': return ['down']; // Never delete account volumes.
    case 'login':
    case 'status': {
      if (!['codex', 'claude'].includes(provider)) throw Error('Choose codex or claude.');
      return ['run', '--rm', '--no-deps', '--entrypoint', `/opt/providers/bin/${provider}`,
        `${provider}-mcp`, ...(provider === 'codex'
          ? ['login', ...(action === 'login' ? ['--device-auth'] : ['status'])]
          : ['auth', action === 'login' ? 'login' : 'status'])];
    }
    default: throw Error('Use check, build, up, down, login <codex|claude> or status <codex|claude>.');
  }
}

// Inspect only the resolved configuration; Compose remains the .env parser.
export function checkDesktopConfiguration(config, pathOptions) {
  const fail = message => { throw Error('Desktop configuration: ' + message); };
  const names = Object.keys(config.services || {}).sort();
  if (names.join(',') !== 'claude-mcp,codex-mcp') fail('exactly two provider services are required.');
  let project;
  for (const name of names) {
    const s = config.services[name], env = s.environment || {}, codex = name === 'codex-mcp';
    if (typeof env.AI_MCP_TOKEN !== 'string' || env.AI_MCP_TOKEN.trim().length < 32) fail('generate a private key of at least 32 characters.');
    if (s.user !== '1000:1000' || !s.read_only || !s.cap_drop?.includes('ALL') || s.cap_add?.length ||
        s.privileged || s.network_mode || s.pid || s.ipc || s.devices?.length || s.volumes_from?.length ||
        s.userns_mode || JSON.stringify(s.security_opt) !== JSON.stringify(['no-new-privileges:true']))
      fail('retain the non-root, read-only, capability and namespace restrictions.');
    if (!(Number(s.mem_limit) > 0 && Number(s.cpus) > 0 && Number(s.pids_limit) > 0)) fail('retain resource limits.');
    if (env.AI_MCP_ALLOWED_ROOTS !== '/workspace' || env.AI_MCP_DEFAULT_CWD !== '/workspace' ||
        env.AI_MCP_READ_ONLY_PROJECTS !== 'true' || env.AI_MCP_WRITE_ROOTS) fail('projects must be read-only at /workspace.');
    if (env.AI_MCP_PROVIDER !== (codex ? 'codex' : 'claude') || env.AI_MCP_PROVIDERS !== env.AI_MCP_PROVIDER ||
        env[codex ? 'AI_MCP_CODEX_REUSE_SESSION' : 'AI_MCP_CLAUDE_REUSE_SESSION'] !== 'false' ||
        env[codex ? 'AI_MCP_CODEX_EPHEMERAL' : 'AI_MCP_CLAUDE_NO_SESSION_PERSISTENCE'] !== 'true')
      fail('provider calls must be independent.');
    const port = s.ports;
    if (port?.length !== 1 || port[0].host_ip !== '127.0.0.1' || port[0].target !== 8080 ||
        String(port[0].published) !== (codex ? '8788' : '8789')) fail('retain the two localhost ports.');
    const volumes = s.volumes || [], bind = volumes.find(v => v.target === '/workspace');
    const home = volumes.find(v => v.target === '/home/agent');
    if (volumes.length !== 2 || bind?.type !== 'bind' || bind.read_only !== true ||
        !bind.bind || bind.bind.create_host_path === true || home?.type !== 'volume' ||
        home.source !== (codex ? 'desktop-codex-home' : 'desktop-claude-home'))
      fail('retain only the selected project and separate credential volumes.');
    const physical = validateProjectDirectory(bind.source, pathOptions);
    if (project && physical !== project) fail('both providers must use the same project directory.');
    project = physical;
  }
  return {services: names, hostProject: project, serverProject: '/workspace', authenticationRequired: true};
}

export function main(args = process.argv.slice(2)) {
  const [action = 'check', provider, ...extra] = args;
  if (extra.length || (!['login', 'status'].includes(action) && provider)) throw Error('Unexpected arguments.');
  const command = desktopCommand(action, provider), compose = desktopArgs();
  const capture = selected => execFileSync('docker', selected,
    {cwd: repository, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe']});
  const engine = JSON.parse(capture(['info', '--format', '{{json .}}']));
  if (engine.OSType !== 'linux' || !['x86_64', 'aarch64', 'amd64', 'arm64'].includes(engine.Architecture))
    throw Error('Use Docker Desktop Linux containers with an amd64 or arm64 engine.');
  if (action !== 'down') {
    const config = JSON.parse(capture([...compose, 'config', '--format', 'json']));
    console.log(JSON.stringify(checkDesktopConfiguration(config)));
  }
  if (action === 'check') return 0;
  if (action === 'login') {
    const running = capture([...compose, 'ps', '--status', 'running', '--services']).trim().split(/\s+/);
    if (running.includes(`${provider}-mcp`)) throw Error('Run desktop.mjs down before logging in to a running provider.');
  }
  if (['up', 'login', 'status'].includes(action)) {
    try { capture(['network', 'inspect', 'ai-mcp-shared']); }
    catch { capture(['network', 'create', 'ai-mcp-shared']); }
  }
  const result = spawnSync('docker', [...compose, ...command], {cwd: repository, stdio: 'inherit'});
  if (result.error) throw result.error;
  return result.status ?? 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { process.exitCode = main(); }
  catch (error) {
    // Expanded Compose output can contain keys; never print subprocess stdout/stderr.
    console.error(error.stdout || error.stderr
      ? 'Docker Desktop command failed. Check Docker Linux-container mode and your private .env.desktop.'
      : error.message);
    process.exitCode = 1;
  }
}
