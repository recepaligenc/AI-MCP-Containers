import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {chromium} from 'playwright';

test('UI inherits the operator default and forwards only explicitly selected client sandboxes', async t => {
  const browser = await chromium.launch({headless: true});
  t.after(() => browser.close());
  const page = await browser.newPage();
  const html = fs.readFileSync(new URL('../wwwroot/index.html', import.meta.url), 'utf8');
  const requests = [];
  await page.route('http://ai-mcp.test/**', async route => {
    const pathname = new URL(route.request().url()).pathname;
    if (pathname === '/') return route.fulfill({contentType: 'text/html', body: html});
    if (pathname === '/auth/verify') return route.fulfill({json: {ok: true, providers: ['codex'],
      defaultProvider: 'codex', defaultSandbox: 'danger-full-access'}});
    if (pathname !== '/chat') return route.fulfill({status: 404});
    requests.push(route.request().postDataJSON());
    return route.fulfill({json: {answer: 'UI_STUB_OK', provider: 'codex', model: 'stub',
      sandbox: 'danger-full-access', durationMs: 1}});
  });
  await page.goto('http://ai-mcp.test/');
  await page.fill('#token', 't'.repeat(32));
  await page.click('#loginBtn');
  await page.locator('#appView').waitFor({state: 'visible'});
  assert.equal(await page.locator('#sandbox').inputValue(), '');
  await page.fill('#prompt', 'A mocked request; no provider is called.');
  await page.click('#sendBtn');
  await page.locator('#result:not(.hidden)').waitFor();
  assert.equal(requests.length, 1);
  assert.equal(Object.hasOwn(requests[0], 'sandbox'), false);
  await page.selectOption('#sandbox', 'read-only');
  await page.click('#sendBtn');
  await page.locator('#result:not(.hidden)').waitFor();
  assert.equal(requests.length, 2);
  assert.equal(requests[1].sandbox, 'read-only');
});
