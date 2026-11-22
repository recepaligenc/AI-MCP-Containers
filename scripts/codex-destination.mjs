import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {hostPaths} from './host-paths.mjs';

export const marker = '.ai-mcp-managed-codex';
const within = (value, root) => value === root || value.startsWith(root + path.sep);
function physical(value) {
  value = path.resolve(value);
  if (fs.existsSync(value)) return fs.realpathSync(value);
  const parent = path.dirname(value);
  return parent === value ? value : path.join(physical(parent), path.basename(value));
}

export function validateDestination({source, destination, extensions, projectRoot, home = os.homedir()}) {
  // A legacy final symlink is removed, never followed during conversion.
  const link = fs.existsSync(destination) && fs.lstatSync(destination).isSymbolicLink();
  const dest = link ? path.join(physical(path.dirname(destination)), path.basename(destination)) : physical(destination);
  const src = physical(source), ext = physical(extensions), owner = physical(home);
  if (dest === '/' || within(owner, dest) || within(src, dest) || within(dest, src) ||
      within(ext, dest) || within(dest, ext) ||
      (projectRoot && (within(physical(projectRoot), dest) || within(dest, physical(projectRoot)))))
    throw Error('The Codex destination must be a dedicated directory outside projects, home roots and extensions.');
  if (!link && fs.existsSync(destination)) {
    if (!fs.statSync(destination).isDirectory()) throw Error('The Codex destination must be a directory.');
    const entries = fs.readdirSync(destination);
    if (entries.includes(marker)) {
      if (fs.readFileSync(path.join(destination, marker), 'utf8') !== 'ai-mcp-containers-managed-codex-v1\n')
        throw Error('The Codex destination has an invalid ownership marker.');
    } else if (entries.length) {
      // Migrate an existing binary-only copy, never adopt an arbitrary nonempty directory.
      const nativeEntries = new Set(fs.readdirSync(source));
      if (!entries.includes('codex') || !fs.statSync(path.join(destination, 'codex')).isFile() ||
          entries.some(name => !nativeEntries.has(name)))
        throw Error('The nonempty Codex destination is not a recognized binary copy. Choose a new dedicated directory.');
    }
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const paths = hostPaths();
    validateDestination({source: process.argv[2], destination: process.argv[3],
      extensions: paths.codexExtensions, projectRoot: paths.projectRoot});
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
