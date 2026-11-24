'use strict';
// Optional browser gate: install Playwright outside the source tree and expose
// it through NODE_PATH. Uses installed Edge by default; only synthetic inputs.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');

(async () => {
  const base = process.argv[2] || 'http://127.0.0.1:18080';
  fs.mkdirSync('artifacts/release-review', { recursive: true });
  const browser = await chromium.launch({ channel: process.env.PWA_BROWSER_CHANNEL || 'msedge', headless: true });
  try {
    for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
      const context = await browser.newContext({ viewport });
      const page = await context.newPage();
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
      await page.goto(base, { waitUntil: 'domcontentloaded' });
      await page.getByRole('heading', { name: 'Unlock your vault' }).waitFor({ timeout: 90000 });
      assert.ok(await page.evaluate(() => Boolean(navigator.serviceWorker.controller)), 'Startup is not worker-controlled');
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'Access screen overflows horizontally');
      assert.equal(await page.locator('#sv-passphrase').count(), 0, 'Device persistence enabled by default');
      await page.locator('.sv-mnemonic-grid input').first().fill('synthetic-input');
      await page.getByRole('button', { name: 'Stellar secret key', exact: true }).click();
      assert.equal(await page.locator('#sv-secret-key').getAttribute('type'), 'password', 'S-key must be masked');
      await page.locator('#sv-secret-key').fill('synthetic-invalid-key');
      await page.getByRole('button', { name: 'Recovery phrase / HD', exact: true }).click();
      assert.equal(await page.locator('.sv-mnemonic-grid input').first().inputValue(), '', 'Old mnemonic input survived strategy switch');
      await page.getByRole('button', { name: 'Stellar secret key', exact: true }).click();
      assert.equal(await page.locator('#sv-secret-key').inputValue(), '', 'Old S-key input survived strategy switch');
      await page.getByRole('button', { name: 'Recovery phrase / HD', exact: true }).click();
      await context.setOffline(true);
      await page.reload({ waitUntil: 'domcontentloaded' });
      await page.getByRole('heading', { name: 'Unlock your vault' }).waitFor({ timeout: 90000 });
      await context.setOffline(false);
      await page.screenshot({ path: `artifacts/release-review/pwa-${viewport.width}.png`, fullPage: true });
      assert.deepEqual(errors, [], 'Browser errors during online/offline access flow');
      console.log(`PASS: ${viewport.width}px startup, controlled worker, overflow, persistence opt-in, masked S-key, strategy switch and offline reload.`);
      await context.close();
    }
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
