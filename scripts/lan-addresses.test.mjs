import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {assertLocalDockerEndpoint, resolveLanAddresses} from './lan-addresses.mjs';

const address = (value, internal = false) => ({address: value, internal});
const interfaces = {
  lo: [address('127.0.0.1', true)],
  eth0: [address('10.0.0.4'), address('203.0.113.4'), address('fe80::1')],
  wlan0: [address('192.168.1.20'), address('10.0.0.4')],
  docker0: [address('172.17.0.1')], 'br-example': [address('172.18.0.1')],
  veth0: [address('172.19.0.1')], virbr0: [address('192.168.122.1')],
  wg0: [address('10.8.0.1')], tun0: [address('10.9.0.1')],
  virtual0: [address('10.10.0.1')]
};
const options = {interfaces, platform: 'linux', isPhysical: name => name !== 'virtual0'};

test('automatic LAN selection returns each physical private IPv4 once and excludes virtual or public addresses', () => {
  assert.deepEqual(resolveLanAddresses('auto', options), ['10.0.0.4', '192.168.1.20']);
});

test('automatic discovery without suitable addresses remains localhost-only and rejects unsupported hosts', () => {
  assert.deepEqual(resolveLanAddresses('auto', {...options, isPhysical: () => false}), []);
  assert.throws(() => resolveLanAddresses('auto', {...options, platform: 'darwin'}), /Linux Docker host/);
});

test('manual LAN selection and empty opt-out retain their explicit behavior', () => {
  assert.deepEqual(resolveLanAddresses('', options), []);
  assert.deepEqual(resolveLanAddresses('192.168.1.20', options), ['192.168.1.20']);
  assert.throws(() => resolveLanAddresses('192.168.1.99', options), /host network interface/);
  for (const selection of ['0.0.0.0', '::', '203.0.113.4', 'localhost'])
    assert.throws(() => resolveLanAddresses(selection, options), /private LAN/);
});

test('automatic discovery requires a local Linux Docker endpoint', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ai-mcp-endpoint-'));
  t.after(() => fs.rmSync(root, {recursive: true, force: true}));
  const socket = path.join(root, 'engine.sock');
  fs.writeFileSync(socket, '');
  assert.doesNotThrow(() => assertLocalDockerEndpoint({endpoint: `unix://${socket}`, context: 'default'}));
  for (const endpoint of ['ssh://remote', 'tcp://192.168.1.50:2376', 'npipe:////./pipe/docker_engine',
    'unix:///home/example/.docker/desktop/docker.sock'])
    assert.throws(() => assertLocalDockerEndpoint({endpoint}), /Automatic LAN discovery/);
  assert.throws(() => assertLocalDockerEndpoint({endpoint: 'unix:///tmp/socket', context: 'desktop-linux'}), /local Docker/);
  assert.throws(() => assertLocalDockerEndpoint({endpoint: 'unix:///tmp/socket', operatingSystem: 'Docker Desktop'}), /local Docker/);
});

test('automatic discovery rejects a local socket symlink pointing to Docker Desktop', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ai-mcp-socket-'));
  t.after(() => fs.rmSync(root, {recursive: true, force: true}));
  const desktop = path.join(root, '.docker/desktop/docker.sock');
  fs.mkdirSync(path.dirname(desktop), {recursive: true});
  fs.writeFileSync(desktop, '');
  const alias = path.join(root, 'docker.sock');
  fs.symlinkSync(desktop, alias);
  assert.throws(() => assertLocalDockerEndpoint({endpoint: `unix://${alias}`, context: 'default'}), /Docker Desktop sockets/);
});
