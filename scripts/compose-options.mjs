import fs from 'node:fs';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {lanAddress} from './host-paths.mjs';

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

export function composeArguments({root = repository, envText, environment = process.env} = {}) {
  const args = ['-f', path.join(root, 'docker-compose.yml')];
  const override = path.join(root, 'docker-compose.override.yml');
  if (fs.existsSync(override)) args.push('-f', override);
  if (lanAddress({root, envText, environment})) args.push('-f', path.join(root, 'docker-compose.lan.yml'));
  return args;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const result = spawnSync('docker', ['compose', ...composeArguments(), ...process.argv.slice(2)],
      {cwd: repository, stdio: 'inherit'});
    if (result.error) throw result.error;
    process.exitCode = result.status ?? 1;
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
