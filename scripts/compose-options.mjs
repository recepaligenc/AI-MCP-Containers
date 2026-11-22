import fs from 'node:fs';
import path from 'node:path';
import {execFileSync, spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {lanAddress} from './host-paths.mjs';
import {assertLocalDockerEndpoint, resolveLanAddresses} from './lan-addresses.mjs';

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

export function dockerEndpoint({environment = process.env, execute = execFileSync} = {}) {
  if (!environment.DOCKER_CONTEXT && environment.DOCKER_HOST)
    return {endpoint: environment.DOCKER_HOST};
  const args = ['context', 'inspect'];
  if (environment.DOCKER_CONTEXT) args.push(environment.DOCKER_CONTEXT);
  args.push('--format', '{{.Name}}|{{.Endpoints.docker.Host}}');
  const [context, endpoint] = execute('docker', args,
    {env: environment, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe']}).trim().split('|');
  return {context, endpoint};
}

export function composePlan({root = repository, envText, environment = process.env,
  interfaces, platform, isPhysical, endpoint} = {}) {
  const selection = lanAddress({root, envText, environment});
  if (selection === 'auto') {
    const daemon = endpoint ?? dockerEndpoint({environment});
    assertLocalDockerEndpoint(daemon);
    if (!endpoint) {
      const operatingSystem = execFileSync('docker', ['info', '--format', '{{.OperatingSystem}}'],
        {env: environment, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe']}).trim();
      assertLocalDockerEndpoint({...daemon, operatingSystem});
    }
  }
  const lanIps = resolveLanAddresses(selection, {interfaces, platform, isPhysical});
  const args = ['--project-directory', root, '-f', path.join(root, 'docker-compose.yml')];
  const override = path.join(root, 'docker-compose.override.yml');
  if (fs.existsSync(override)) args.push('-f', override);
  let input = '';
  if (lanIps.length) {
    args.push('-f', '-');
    input = JSON.stringify({services: Object.fromEntries(['codex-mcp', 'claude-mcp'].map((name, index) =>
      [name, {ports: lanIps.map(ip => ({host_ip: ip, target: 8080,
        published: String(8788 + index), protocol: 'tcp'}))}]))});
  }
  return {args, input, lanIps, selection};
}

export function runCompose(plan, commands) {
  const result = spawnSync('docker', ['compose', ...plan.args, ...commands],
    {cwd: repository, input: plan.input, stdio: ['pipe', 'inherit', 'inherit']});
  if (result.error) throw result.error;
  return result.status ?? 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    process.exitCode = runCompose(composePlan(), process.argv.slice(2));
  } catch (error) {
    console.error(error.stdout || error.stderr ? 'The local Docker context could not be resolved.' : error.message);
    process.exitCode = 1;
  }
}
