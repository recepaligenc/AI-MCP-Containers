import fs from 'node:fs';
import os from 'node:os';
import {isIPv4} from 'node:net';

function privateIPv4(value) {
  if (!isIPv4(value)) return false;
  const [a, b] = value.split('.').map(Number);
  return a === 10 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168);
}

function hostEntries(interfaces) {
  return Object.entries(interfaces).filter(([name]) => !/^(lo|docker|br-|veth|virbr)/.test(name));
}

function activePhysicalInterface(name) {
  const directory = `/sys/class/net/${name}`;
  try {
    return fs.existsSync(`${directory}/device`) &&
      ['up', 'unknown'].includes(fs.readFileSync(`${directory}/operstate`, 'utf8').trim());
  } catch { return false; }
}

export function checkLanAddress(value, interfaces = os.networkInterfaces()) {
  if (!privateIPv4(value)) throw Error('Select a private LAN IPv4 address; wildcard and public bindings are forbidden.');
  if (!hostEntries(interfaces).some(([, entries]) =>
    entries.some(entry => entry.address === value && !entry.internal)))
    throw Error('The selected LAN IPv4 address must belong to a host network interface.');
}

export function resolveLanAddresses(selection, {interfaces, platform = os.platform(),
  isPhysical = activePhysicalInterface} = {}) {
  if (!selection) return [];
  if (selection === 'auto' && platform !== 'linux')
    throw Error('Automatic LAN discovery currently requires a Linux Docker host.');
  interfaces ??= os.networkInterfaces();
  if (selection !== 'auto') {
    checkLanAddress(selection, interfaces);
    return [selection];
  }
  const addresses = hostEntries(interfaces)
    .filter(([name]) => !/^(tun|tap|wg|tailscale|zt|vpn)/i.test(name) && isPhysical(name))
    .flatMap(([, entries]) => entries.filter(entry => !entry.internal && privateIPv4(entry.address))
      .map(entry => entry.address));
  return [...new Set(addresses)].sort();
}

export function assertLocalDockerEndpoint({endpoint, context, operatingSystem} = {}) {
  if (typeof endpoint !== 'string' || !endpoint.startsWith('unix://') || /[\r\n\0]/.test(endpoint) ||
      context?.startsWith('desktop') || /docker desktop/i.test(operatingSystem || ''))
    throw Error('Automatic LAN discovery requires a local Docker Unix socket, without a remote or Docker Desktop context.');
  const url = new URL(endpoint);
  if (url.hostname) throw Error('Automatic LAN discovery requires a local Docker Unix socket.');
  let socket = decodeURIComponent(url.pathname);
  try { socket = fs.realpathSync(socket); }
  catch (error) {
    if (error.code !== 'ENOENT') throw Error('The local Docker socket path could not be verified.');
  }
  if (/\/\.docker\/desktop\//.test(socket))
    throw Error('Automatic LAN discovery does not support Docker Desktop sockets.');
}
