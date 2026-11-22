import pkg from './node_modules/playwright/index.js';
const { chromium } = pkg;

const BASE = 'http://127.0.0.1:8789';
const TOKEN = 'test-secret-123';
const shot = (p) => `./shots/cb-ui-${p}.png`;

const browser = await chromium.launch();
const results = [];
function check(name, cond, detail = '') {
  results.push({ name, ok: !!cond, detail });
  console.log(`${cond ? 'PASS' : 'FAIL'} — ${name}${detail ? ' :: ' + detail : ''}`);
}

try {
  // ---- 1. Login screen renders, app view hidden ----
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const page = await ctx.newPage();
  await page.goto(BASE, { waitUntil: 'networkidle' });

  await page.waitForSelector('#loginView', { state: 'visible' });
  const appHiddenAtStart = await page.locator('#appView').isHidden();
  check('login view visible on load', await page.locator('#loginView').isVisible());
  check('app view hidden before login', appHiddenAtStart);
  check('title correct', (await page.title()) === 'AI-MCP-Containers', await page.title());
  await page.screenshot({ path: shot('1-login') });

  // ---- 2. Wrong token is rejected, stays on login ----
  await page.fill('#token', 'totally-wrong');
  await page.click('#loginBtn');
  await page.waitForSelector('#loginMsg.show', { state: 'visible' });
  const errText = await page.locator('#loginMsg').textContent();
  check('wrong token shows error', /invalid token/i.test(errText), errText.trim());
  check('still on login after bad token', await page.locator('#loginView').isVisible());
  await page.screenshot({ path: shot('2-bad-token') });

  // ---- 3. Correct token logs in, reveals app view ----
  await page.fill('#token', TOKEN);
  await page.click('#loginBtn');
  await page.waitForSelector('#appView', { state: 'visible', timeout: 10000 });
  check('app view visible after login', await page.locator('#appView').isVisible());
  check('login view hidden after login', await page.locator('#loginView').isHidden());
  check('session/logout shown', await page.locator('#logoutBtn').isVisible());
  await page.screenshot({ path: shot('3-app') });

  // ---- 4. Send a real test prompt → BRIDGE_OK round-trip ----
  await page.fill('#prompt', 'Reply with exactly: BRIDGE_OK');
  await page.click('#sendBtn');
  await page.waitForSelector('#result:not(.hidden)', { state: 'visible', timeout: 120000 });
  const answer = (await page.locator('#rAnswer').textContent()).trim();
  const dur = (await page.locator('#rDuration').textContent()).trim();
  check('round-trip answer is BRIDGE_OK', answer === 'BRIDGE_OK', `answer="${answer}" took ${dur}`);
  check('success banner shown', /success/i.test(await page.locator('#chatMsg').textContent()));
  await page.screenshot({ path: shot('4-result') });

  // ---- 5. XSS safety: HTML in answer is rendered as text, not executed ----
  // (Verify the answer node uses textContent — inject via prompt echo.)
  await page.fill('#prompt', 'Reply with exactly: <img src=x onerror=alert(1)>');
  await page.click('#sendBtn');
  await page.waitForSelector('#result:not(.hidden)', { state: 'visible', timeout: 120000 });
  const rawHtml = await page.locator('#rAnswer').innerHTML();
  const hasLiveImg = await page.locator('#rAnswer img').count();
  check('answer HTML is escaped (no live <img>)', hasLiveImg === 0, `imgCount=${hasLiveImg}`);
  check('answer shows escaped markup', rawHtml.includes('&lt;img'), rawHtml.slice(0, 60));

  // ---- 6. Logout returns to login ----
  await page.click('#logoutBtn');
  await page.waitForSelector('#loginView', { state: 'visible' });
  check('logout returns to login', await page.locator('#loginView').isVisible());

  // ---- 7. Responsive: narrow viewport, no horizontal overflow ----
  await ctx.close();
  for (const [w, h, tag] of [[390, 844, 'mobile'], [834, 1112, 'tablet']]) {
    const c = await browser.newContext({ viewport: { width: w, height: h } });
    const p = await c.newPage();
    await p.goto(BASE, { waitUntil: 'networkidle' });
    const overflow = await p.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
    check(`no horizontal overflow @ ${tag} (${w}px)`, !overflow);
    await p.screenshot({ path: shot(`5-${tag}`) });
    await c.close();
  }

  const failed = results.filter(r => !r.ok);
  console.log(`\n=== ${results.length - failed.length}/${results.length} checks passed ===`);
  process.exit(failed.length ? 1 : 0);
} catch (e) {
  console.error('SCRIPT ERROR:', e.message);
  process.exit(2);
} finally {
  await browser.close();
}
