import { chromeExecutable, requireMemory } from './runtime.mjs';
requireMemory();
import { chromium } from 'playwright-core';
import { build } from '../../extensions/graphbridge-query-lab/node_modules/esbuild/lib/main.js';
import { createRequire } from 'node:module';
import { createServer } from 'node:http';
import { resolve } from 'node:path';
import { readFile, writeFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
const root = resolve(import.meta.dirname, '../..');
const artifacts = resolve(root, '.artifacts/vscode');
const extension = resolve(root, 'extensions/graphbridge-query-lab');
await build({ entryPoints: [resolve(extension, 'src/evidence.ts')], outfile: resolve(artifacts, 'parser.cjs'), bundle: true, platform: 'node', format: 'cjs' });
const { parseEvidence } = createRequire(import.meta.url)(resolve(artifacts, 'parser.cjs'));
const compiled = parseEvidence(await readFile(resolve(artifacts, 'compiled-evidence.json'), 'utf8'));
const semantic = parseEvidence(await readFile(resolve(root, 'tests/GraphBridge.QueryLab.Tests/fixtures/verify-semantic.json'), 'utf8'));
const requests = [];
const server = createServer(async (req, res) => {
  if (req.url === '/') { res.setHeader('Content-Type', 'text/html'); res.end(`<!doctype html><html><head>
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'nonce-test-only'; style-src 'self'; connect-src 'none'; img-src 'none'; base-uri 'none'; form-action 'none'">
    <link rel="stylesheet" href="/webview.css"></head><body><div id="root"></div><script nonce="test-only" src="/webview.js"></script></body></html>`); }
  else if (req.url === '/webview.js' || req.url === '/webview.css') { res.setHeader('Content-Type', req.url.endsWith('.js') ? 'text/javascript' : 'text/css'); res.end(await readFile(resolve(extension, 'dist' + req.url))); }
  else { res.statusCode = 404; res.end(); }
});
await new Promise(r => server.listen(0, '127.0.0.1', r));
const origin = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch({ executablePath: chromeExecutable(), headless: true,
  args: ['--disable-background-networking', '--disable-component-update', '--disable-sync'] });
const observations = [];
try {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  await context.route('**/*', route => { requests.push(route.request().url()); return route.request().url().startsWith(origin + '/') ? route.continue() : route.abort(); });
  await context.addInitScript(data => {
    window.acquireVsCodeApi = () => ({ postMessage(message) { if (message.type === 'ready') window.postMessage({ type: 'evidence', data }, '*'); } });
  }, compiled);
  const page = await context.newPage();
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  await page.goto(origin);
  await page.getByRole('heading', { name: 'See the query. Understand the evidence.' }).waitFor();
  await page.getByLabel('Query contract').selectOption('movies.optional.v1');
  await page.getByRole('button', { name: '2. OPTIONAL-MATCH', exact: true }).focus();
  await page.keyboard.press('Enter');
  assert.equal(await page.getByRole('button', { name: '2. OPTIONAL-MATCH', exact: true }).getAttribute('aria-pressed'), 'true');
  assert.ok(await page.locator('.selected-line').count() > 0);
  await page.getByText('Accessible pattern description', { exact: true }).click();
  assert.match(await page.locator('.pattern details').innerText(), /Physical endpoints/);
  observations.push('keyboard clause selection and complete textual graph alternative');
  await page.screenshot({ path: resolve(artifacts, 'browser-query-dark.png'), fullPage: true });
  await page.setViewportSize({ width: 420, height: 850 });
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
  await page.screenshot({ path: resolve(artifacts, 'browser-query-narrow.png'), fullPage: true });
  await page.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' });
  await page.screenshot({ path: resolve(artifacts, 'browser-query-contrast.png'), fullPage: true });
  observations.push('narrow layout no document overflow, forced-colors screenshot');
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.emulateMedia({ forcedColors: 'none' });
  await page.evaluate(data => window.postMessage({ type: 'evidence', data }, '*'), semantic);
  await page.getByRole('button', { name: 'Verify', exact: true }).click();
  await page.getByRole('heading', { name: 'Transaction cleanup' }).waitFor();
  assert.equal(await page.locator('tbody tr').count(), 3);
  assert.match(await page.locator('tbody').innerText(), /Expected rows: 8 · observed: 8/);
  await page.screenshot({ path: resolve(artifacts, 'browser-verify.png'), fullPage: true });
  await page.getByRole('button', { name: 'Doctor', exact: true }).click();
  assert.ok(await page.locator('tbody tr').count() > 0);
  await page.getByLabel('Find findings').fill('IdentityConstraintPresent');
  assert.equal(await page.locator('tbody tr').count(), 1);
  await page.screenshot({ path: resolve(artifacts, 'browser-doctor.png'), fullPage: true });
  observations.push('real V2 native/fixed and Fluent/native rows, aggregated schema filter');
  const bad = structuredClone(compiled); bad.sources[0].text = '<img src="https://example.invalid/exfil"><script>window.evil=true</script>';
  await page.evaluate(data => window.postMessage({ type: 'evidence', data }, '*'), bad);
  await page.getByText(bad.sources[0].text, { exact: true }).waitFor();
  assert.equal(await page.locator('img').count(), 0);
  assert.equal(await page.evaluate(() => window.evil), undefined);
  await page.evaluate(() => { const script = document.createElement('script'); script.textContent = 'window.evil=true'; document.body.append(script); });
  assert.equal(await page.evaluate(() => window.evil), undefined);
  assert.ok(requests.every(r => r.startsWith(origin + '/')));
  assert.deepEqual(errors, []);
  observations.push('hostile source escaped, CSP blocks non-nonced inline script, no external requests or JS errors');
  await writeFile(resolve(artifacts, 'browser-receipt.json'), JSON.stringify({ status: 'pass', observations, requests }, null, 2));
  console.log('Browser interaction/security/layout checks passed: ' + observations.join('; '));
} finally { await browser.close(); await new Promise(r => server.close(r)); }
