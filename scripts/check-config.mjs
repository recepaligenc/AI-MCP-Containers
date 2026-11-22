import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {execFileSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {hostPaths} from './host-paths.mjs';
import {composePlan, runCompose} from './compose-options.mjs';
import {checkLanAddress} from './lan-addresses.mjs';
export {checkLanAddress} from './lan-addresses.mjs';

export function checkConfiguration(config, selectedPaths = hostPaths(), {lanIps = [], interfaces} = {}) {
  if (new Set(lanIps).size !== lanIps.length) throw Error('Duplicate LAN bindings are forbidden.');
  for (const ip of lanIps) checkLanAddress(ip, interfaces);
  const names = Object.keys(config.services || {}).sort();
  if (names.join(',') !== 'claude-mcp,codex-mcp') throw Error('Exactly the two provider services are required.');
  const summaries = [];
  for (const name of names) {
    const service = config.services[name], env = service.environment || {};
    const fail = message => { throw Error(name + ': ' + message); };
    if (typeof env.AI_MCP_TOKEN !== 'string' || env.AI_MCP_TOKEN.trim().length < 32)
      fail('Generate a private AI_MCP_TOKEN containing at least 32 characters.');
    if (Object.keys(env).some(key => key.startsWith('CODEX_BRIDGE_') || key.startsWith('BRIDGE_')))
      fail('Remove deprecated environment names.');
    if (service.privileged || service.network_mode || service.pid || service.ipc === 'host' ||
        service.userns_mode === 'host' || service.volumes_from?.length || service.devices?.length)
      fail('Privileged execution, host namespaces and additional host devices are forbidden.');
    if (env.AI_MCP_READ_ONLY_PROJECTS !== 'true' || env.AI_MCP_WRITE_ROOTS)
      fail('Project write access must be disabled.');
    const codex = name === 'codex-mcp';
    const addedCaps = (service.cap_add || []).map(c => c.replace(/^CAP_/, '')).sort();
    const expectedCaps = codex ? [] : ['CHOWN', 'DAC_OVERRIDE', 'SETGID', 'SETUID'];
    if (JSON.stringify(addedCaps) !== JSON.stringify(expectedCaps) ||
        service.security_opt?.some(option => option !== 'no-new-privileges:true'))
      fail('Do not weaken the capability or runtime security policy.');
    if (codex && !/^[1-9][0-9]*:[0-9]+$/.test(service.user || ''))
      fail('The Codex service must use a non-root host credential owner.');
    if (!codex && (!/^[1-9][0-9]*$/.test(env.AI_MCP_HOST_UID || '') ||
        !/^[0-9]+$/.test(env.AI_MCP_HOST_GID || '')))
      fail('Claude must drop privileges to a non-root host credential owner.');
    if (env[codex ? 'AI_MCP_CODEX_REUSE_SESSION' : 'AI_MCP_CLAUDE_REUSE_SESSION'] !== 'false' ||
        env[codex ? 'AI_MCP_CODEX_EPHEMERAL' : 'AI_MCP_CLAUDE_NO_SESSION_PERSISTENCE'] !== 'true')
      fail('Provider calls must be independent and must disable session persistence.');
    const root = env.AI_MCP_ALLOWED_ROOTS;
    if (!root || root.includes(';') || !path.isAbsolute(root) || !fs.statSync(root).isDirectory())
      fail('Select one existing absolute project root.');
    const physical = fs.realpathSync(root), home = fs.realpathSync(os.homedir());
    if (physical === '/' || home === physical || home.startsWith(physical + path.sep))
      fail('The project root must not expose the entire host or home directory.');
    if (root !== selectedPaths.projectRoot) fail('Use the project root selected in the host environment.');
    if (env.AI_MCP_DEFAULT_CWD !== root) fail('The default cwd must be the selected project root.');
    if (service.read_only !== true || !service.security_opt?.includes('no-new-privileges:true'))
      fail('A read-only runtime filesystem and no-new-privileges are required.');
    if (!service.cap_drop?.includes('ALL') || !(Number(service.mem_limit) > 0) ||
      !(Number(service.cpus) > 0) || !(Number(service.pids_limit) > 0)) fail('Runtime resource/capability limits are required.');
    const expectedHosts = ['127.0.0.1', ...lanIps];
    const ports = service.ports || [];
    if (ports.length !== expectedHosts.length || expectedHosts.some(ip => ports.filter(port => port.host_ip === ip).length !== 1) ||
      ports.some(port => String(port.published) !== (codex ? '8788' : '8789') || port.target !== 8080 ||
        (port.protocol && port.protocol !== 'tcp')))
      fail('Keep the localhost mapping and only the selected private LAN mappings on the provider port.');
    const projectBinds = (service.volumes || []).filter(v => v.type === 'bind' && v.target === root);
    if (projectBinds.length !== 1 || projectBinds[0].source !== root || projectBinds[0].read_only !== true)
      fail('The project bind must use the selected path and be read-only.');
    const expectedBinds = codex
      ? new Map([[root, root], ['/opt/codex', selectedPaths.codexBinary], [selectedPaths.codexHome, selectedPaths.codexHome]])
      : new Map([[root, root], ['/vscode-extensions', selectedPaths.claudeExtensions], ['/seed', selectedPaths.claudeSeed]]);
    if (codex && env.AI_MCP_CODEX_HOME !== selectedPaths.codexHome)
      fail('The native Codex home must be the host credential directory.');
    const allowedTargets = new Set([...expectedBinds.keys(), ...(!codex ? ['/claudehome'] : [])]);
    const seen = new Set();
    for (const volume of service.volumes || []) {
      if (!allowedTargets.has(volume.target)) fail('Unexpected volume target; every mounted path is an access grant.');
      if (seen.has(volume.target)) fail('Duplicate volume targets are forbidden.');
      seen.add(volume.target);
      if (volume.type === 'bind') {
        if (volume.source !== expectedBinds.get(volume.target))
          fail('Host bind sources must match the selected project, provider and credential paths.');
        if (volume.bind?.create_host_path === true) fail('Docker must not create host bind directories.');
        const authWrite = name === 'codex-mcp' && volume.target === env.AI_MCP_CODEX_HOME;
        if (!authWrite && volume.read_only !== true) fail('Only the shared Codex credential directory may be a writable host bind.');
        if (!fs.existsSync(volume.source)) fail('A selected host bind directory is missing.');
        const source = fs.realpathSync(volume.source);
        if (volume.target !== root && (source === '/' || source === home || home.startsWith(source + path.sep)))
          fail('A provider bind must not expose the entire host or home directory.');
      } else if (!(name === 'claude-mcp' && volume.type === 'volume' && volume.target === '/claudehome')) {
        fail('Unexpected writable runtime volume.');
      }
    }
    if (seen.size !== allowedTargets.size) fail('Required project or provider mounts are missing.');
    summaries.push({service: name, projectRoot: root, projectReadOnly: true,
      defaultCwd: root, authenticationRequired: true, localhostOnly: !lanIps.length, lanAddresses: lanIps});
  }
  return summaries;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
    if (process.argv[2] && process.argv[2] !== '--up') throw Error('Use --up to start the validated services.');
    const plan = composePlan();
    const config = JSON.parse(execFileSync('docker', ['compose', ...plan.args, 'config', '--format', 'json'],
      {cwd: root, input: plan.input, encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe']}));
    console.log(JSON.stringify({passed: true, lanSelection: plan.selection,
      services: checkConfiguration(config, hostPaths(), {lanIps: plan.lanIps})}, null, 2));
    if (plan.selection === 'auto' && !plan.lanIps.length)
      console.error('No active physical private LAN IPv4 addresses found; localhost and Docker-network access remain available.');
    if (process.argv[2] === '--up') process.exitCode = runCompose(plan, ['up', '-d', ...process.argv.slice(3)]);
  } catch (error) {
    // Compose output can contain expanded credentials. Never print stdout/stderr from it.
    console.error(error.stdout || error.stderr ? 'Compose configuration could not be resolved. Check the private .env file.' : error.message);
    process.exitCode = 1;
  }
}
