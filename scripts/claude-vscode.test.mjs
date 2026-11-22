import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {EventEmitter} from 'node:events';
import {spawn} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {selectClaude, runClaude} from './claude-vscode.mjs';

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'claude-vscode-'));
  t.after(() => fs.rmSync(root, {recursive: true, force: true}));
  const install = (version, arch = process.arch, body = '#!/bin/sh\nexit 0\n') => {
    const dir = path.join(root, 'anthropic.claude-code-' + version + '-linux-' + arch);
    fs.mkdirSync(path.join(dir, 'resources/native-binary'), {recursive: true});
    fs.writeFileSync(path.join(dir, 'package.json'), JSON.stringify({publisher: 'Anthropic', name: 'claude-code', version}));
    const executable = path.join(dir, 'resources/native-binary/claude');
    fs.writeFileSync(executable, body, {mode: 0o755});
    return {dir, executable};
  };
  return {root, install};
}

test('selects the highest numeric installed version and observes subsequent updates', t => {
  const f = fixture(t); f.install('2.1.99'); f.install('2.1.325'); f.install('2.1.319');
  assert.equal(selectClaude(f.root).version, '2.1.325');
  f.install('2.1.326'); assert.equal(selectClaude(f.root).version, '2.1.326');
  f.install('2.2.0'); assert.equal(selectClaude(f.root).version, '2.2.0');
});
test('ignores another architecture, unrelated extensions and prereleases', t => {
  const f = fixture(t); f.install('2.1.325'); f.install('9.0.0', process.arch === 'x64' ? 'arm64' : 'x64');
  fs.mkdirSync(path.join(f.root, 'anthropic.claude-code-9.0.0-beta-linux-' + process.arch));
  fs.mkdirSync(path.join(f.root, 'other.claude-code-99.0.0-linux-' + process.arch));
  assert.equal(selectClaude(f.root).version, '2.1.325');
});
test('missing compatible installation or unsupported platform fails without a bundled fallback', t => {
  const f = fixture(t);
  assert.throws(() => selectClaude(f.root), /No compatible/);
  assert.throws(() => selectClaude(f.root, {platform: 'darwin', arch: 'arm64'}), /requires a Linux/);
  assert.throws(() => selectClaude(f.root, {platform: 'linux', arch: 'ia32'}), /requires a Linux/);
});
test('ignores numeric prereleases marked in VSIX, package or installation metadata', t => {
  for (const marker of ['vsix','package','registry']) {
    const f=fixture(t); const stable=f.install('2.1.325'), preview=f.install('2.1.326');
    if(marker==='vsix') fs.writeFileSync(path.join(preview.dir,'.vsixmanifest'),
      '<PackageManifest><Properties><Property Value="true" Id="Microsoft.VisualStudio.Code.PreRelease" /></Properties></PackageManifest>');
    if(marker==='package') fs.writeFileSync(path.join(preview.dir,'package.json'),JSON.stringify({publisher:'Anthropic',name:'claude-code',version:'2.1.326',__metadata:{isPreReleaseVersion:true}}));
    if(marker==='registry') fs.writeFileSync(path.join(f.root,'extensions.json'),JSON.stringify([
      {identifier:{id:'anthropic.claude-code'},version:'2.1.325',relativeLocation:path.basename(stable.dir),metadata:{isPreReleaseVersion:false,preRelease:true}},
      {identifier:{id:'anthropic.claude-code'},version:'2.1.326',relativeLocation:path.basename(preview.dir),metadata:{isPreReleaseVersion:true}}
    ]));
    assert.equal(selectClaude(f.root).version,'2.1.325');
  }
});
test('obsolete and unregistered numeric folders are not active installations', t => {
  const f=fixture(t), stable=f.install('2.1.325'), removed=f.install('2.1.326');
  fs.writeFileSync(path.join(f.root,'.obsolete'),JSON.stringify({[path.basename(removed.dir)]:true}));
  assert.equal(selectClaude(f.root).version,'2.1.325');
  fs.unlinkSync(path.join(f.root,'.obsolete'));
  fs.writeFileSync(path.join(f.root,'extensions.json'),JSON.stringify([
    {identifier:{id:'anthropic.claude-code'},version:'2.1.325',relativeLocation:path.basename(stable.dir),metadata:{isPreReleaseVersion:false}}
  ]));
  assert.equal(selectClaude(f.root).version,'2.1.325');
  fs.writeFileSync(path.join(f.root,'extensions.json'),'[]'); assert.throws(()=>selectClaude(f.root),/No compatible stable/);
});
test('invalid or linked metadata cannot silently authorize an older binary', t => {
  for(const file of ['.obsolete','extensions.json']) {
    const f=fixture(t); f.install('2.1.325');
    fs.writeFileSync(path.join(f.root,'target'),file==='.obsolete'?'{}':'[]'); fs.symlinkSync(path.join(f.root,'target'),path.join(f.root,file));
    assert.throws(()=>selectClaude(f.root),/linked or invalid/);
  }
  const f=fixture(t); f.install('2.1.325'); const newest=f.install('2.1.326');
  fs.writeFileSync(path.join(newest.dir,'.vsixmanifest'),'<Property Id="Microsoft.VisualStudio.Code.PreRelease" Value="unknown" />');
  assert.throws(()=>selectClaude(f.root),/refusing an older CLI/);
});
test('prerelease-only installations fail without a bundled fallback', t => {
  const f=fixture(t), preview=f.install('2.1.326');
  fs.writeFileSync(path.join(preview.dir,'.vsixmanifest'),"<Property Id='Microsoft.VisualStudio.Code.PreRelease' Value='true' />");
  assert.throws(()=>selectClaude(f.root),/No compatible stable/);
});
for (const defect of ['missing', 'identity', 'version', 'executable', 'directory-link', 'binary-link', 'ancestor-link']) {
  test('newest installation with ' + defect + ' is refused instead of silently using the old CLI', t => {
    const f = fixture(t); f.install('2.1.197'); const current = f.install('2.1.325');
    if (defect === 'missing') fs.unlinkSync(current.executable);
    if (defect === 'identity' || defect === 'version') fs.writeFileSync(path.join(current.dir, 'package.json'),
      JSON.stringify({publisher: defect === 'identity' ? 'other' : 'anthropic', name: 'claude-code', version: '2.1.197'}));
    if (defect === 'executable') fs.chmodSync(current.executable, 0o644);
    if (defect === 'directory-link') { fs.renameSync(current.dir, current.dir + '-target'); fs.symlinkSync(current.dir + '-target', current.dir); }
    if (defect === 'binary-link') { fs.renameSync(current.executable, current.executable + '.target'); fs.symlinkSync(current.executable + '.target', current.executable); }
    if (defect === 'ancestor-link') { const dir = path.dirname(current.executable); fs.renameSync(dir, dir + '-target'); fs.symlinkSync(dir + '-target', dir); }
    assert.throws(() => selectClaude(f.root), /refusing an older CLI/);
  });
}
test('preserves exact argv and environment and forwards owner cancellation', async t => {
  const f = fixture(t), selected = f.install('2.1.325'), signals = new EventEmitter(), child = new EventEmitter();
  const killed = []; child.kill = s => killed.push(s);
  const argv = ['-p', '--model', 'claude-opus-5-5', '--tools', '', 'literal $(); text'], env = {HOME: '/fixture-home'};
  let captured;
  const result = runClaude(argv, {root: f.root, environment: env, signals,
    spawnImpl: (...args) => { captured = args; return child; }});
  signals.emit('SIGTERM'); signals.emit('SIGINT'); signals.emit('SIGHUP');
  assert.deepEqual(killed, ['SIGTERM', 'SIGINT','SIGHUP']);
  assert.deepEqual(captured, [selected.executable, argv, {env, stdio: 'inherit'}]);
  child.emit('close', null, 'SIGTERM'); assert.deepEqual(await result, {code: null, signal: 'SIGTERM'});
  assert.equal(signals.listenerCount('SIGTERM'), 0);
});
test('a spawn failure is reported and signal handlers are removed', async t => {
  const f = fixture(t); f.install('2.1.325'); const child = new EventEmitter(), signals = new EventEmitter();
  const result = runClaude([], {root: f.root, signals, spawnImpl: () => child});
  child.emit('error', Error('fixture spawn failure'));
  await assert.rejects(result, /fixture spawn failure/); assert.equal(signals.listenerCount('SIGINT'), 0);
});
test('the real launcher transports stdin and retains a failed CLI exit status', async t => {
  const f = fixture(t); f.install('2.1.325', process.arch, '#!/bin/sh\ncat\nexit 7\n');
  const launcher = fileURLToPath(new URL('./claude-vscode.mjs', import.meta.url));
  const result = await new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [launcher, '--model', 'claude-opus-5-5'],
      {env: {...process.env, AI_MCP_CLAUDE_EXTENSIONS: f.root}, stdio: ['pipe', 'pipe', 'pipe']});
    let output = ''; child.stdout.on('data', b => { output += b; }); child.on('error', reject);
    child.on('close', (code, signal) => resolve({code, signal, output})); child.stdin.end('fixture prompt\n');
  });
  assert.deepEqual(result, {code: 7, signal: null, output: 'fixture prompt\n'});
});
