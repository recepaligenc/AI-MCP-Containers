import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import assert from 'node:assert/strict';
import {validateDestination} from './codex-destination.mjs';

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ai-mcp-dest-'));
  t.after(() => fs.rmSync(root, {recursive: true, force: true}));
  const home = path.join(root, 'home'), extensions = path.join(home, 'extensions');
  const source = path.join(extensions, 'provider', 'bin'), projectRoot = path.join(root, 'projects');
  for (const p of [source, projectRoot]) fs.mkdirSync(p, {recursive: true});
  fs.writeFileSync(path.join(source, 'codex'), 'native-fixture');
  return {home, extensions, source, projectRoot, destination: path.join(home, 'stable')};
}

test('rejects destructive destinations and source overlaps before touching host files', t => {
  const options = fixture(t);
  for (const destination of ['/', options.home, options.projectRoot, path.join(options.projectRoot, 'nested'),
    options.extensions, options.source, path.join(options.source, 'nested')])
    assert.throws(() => validateDestination({...options, destination}), /dedicated directory/);
});

test('rejects unknown nonempty directories and resolves ancestor symlinks', t => {
  const options = fixture(t);
  fs.mkdirSync(options.destination);
  fs.writeFileSync(path.join(options.destination, 'keep-me.txt'), 'user-data');
  assert.throws(() => validateDestination(options), /not a recognized binary copy/);
  assert.equal(fs.readFileSync(path.join(options.destination, 'keep-me.txt'), 'utf8'), 'user-data');
  const alias = path.join(options.home, 'alias');
  fs.symlinkSync(options.projectRoot, alias);
  assert.throws(() => validateDestination({...options, destination: path.join(alias, 'nested')}), /dedicated directory/);
});

test('accepts a new dedicated directory, legacy binary copy and final symlink conversion', t => {
  const options = fixture(t);
  assert.doesNotThrow(() => validateDestination(options));
  fs.mkdirSync(options.destination);
  fs.copyFileSync(path.join(options.source, 'codex'), path.join(options.destination, 'codex'));
  assert.doesNotThrow(() => validateDestination(options));
  fs.rmSync(options.destination, {recursive: true});
  fs.symlinkSync(options.source, options.destination);
  assert.doesNotThrow(() => validateDestination(options));
});
