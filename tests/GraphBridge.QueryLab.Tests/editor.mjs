import { codeExecutable, requireMemory } from './runtime.mjs';
requireMemory(6.5);
import { runTests } from '@vscode/test-electron';
import { spawn } from 'node:child_process';
import { chromium } from 'playwright-core';
import { resolve } from 'node:path';
import { mkdir, rm, writeFile, readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
const root = resolve(import.meta.dirname, '../..');
const artifacts = resolve(root, '.artifacts/vscode');
delete process.env.ELECTRON_RUN_AS_NODE;
const restricted = process.env.GRAPHBRIDGE_TEST_UNTRUSTED === '1';
const runId = new Date().toISOString().replace(/[:.]/g, '-');
const screenshotPrefix = `editor-${restricted ? 'restricted' : 'trusted'}-${runId}`;
const screenshots = [];
const profile = resolve(artifacts, restricted ? 'profile-untrusted' : 'profile');
await mkdir(resolve(profile, 'User'), { recursive: true });
await writeFile(resolve(profile, 'User/settings.json'), JSON.stringify({
  'telemetry.telemetryLevel': 'off', 'update.mode': 'none', 'extensions.autoUpdate': false,
  'extensions.autoCheckUpdates': false, 'git.enabled': false, 'chat.disableAIFeatures': true,
  'workbench.colorTheme': restricted ? 'Default Dark Modern' : 'Default Light Modern',
  'security.workspace.trust.startupPrompt': 'never', 'workbench.startupEditor': 'none'
}));
await mkdir(resolve(artifacts, 'workspace'), { recursive: true });
for (const name of ['editor-ready', 'editor-finish', 'editor-closed']) await rm(resolve(artifacts, name), { force: true });
const options = { vscodeExecutablePath: codeExecutable(),
  extensionDevelopmentPath: process.env.GRAPHBRIDGE_TEST_EXTENSION_PATH ?? resolve(artifacts, 'extensions/joslat.graphbridge-query-lab-0.1.0'),
  extensionTestsPath: resolve(import.meta.dirname, 'editor-host.cjs'),
  extensionTestsEnv: { GRAPHBRIDGE_ROOT: root, GRAPHBRIDGE_TEST_UNTRUSTED: restricted ? '1' : '0' },
  launchArgs: [resolve(artifacts, 'workspace'), '--user-data-dir', profile,
    '--extensions-dir', resolve(artifacts, 'extensions'), ...restricted ? [] : ['--disable-workspace-trust'], '--skip-welcome',
    '--skip-release-notes', '--disable-updates', '--disable-telemetry', '--disable-gpu',
    '--remote-debugging-address=127.0.0.1', '--remote-debugging-port=58183'], timeout: 90000 };
// Official test-electron unconditionally disables trust. Use the same installed executable directly
// for the restricted-workspace case so the acceptance actually exercises workspace trust.
const startedAt = performance.now();
const editor = restricted ? new Promise((done, fail) => {
  const child = spawn(options.vscodeExecutablePath, [...options.launchArgs,
    '--extensionTestsPath=' + options.extensionTestsPath, '--extensionDevelopmentPath=' + options.extensionDevelopmentPath],
    { windowsHide: true, env: { ...process.env, ...options.extensionTestsEnv }, stdio: 'inherit' });
  const timer = setTimeout(() => {
    if (child.pid && child.exitCode === null) spawn('C:/Windows/System32/taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
    fail(new Error('Owned restricted test editor exceeded 90 seconds.'));
  }, 90000);
  child.once('error', error => { clearTimeout(timer); fail(error); });
  child.once('close', code => { clearTimeout(timer); code === 0 ? done() : fail(new Error(`Restricted editor exited ${code}`)); });
}) : runTests(options);
// Attach a rejection handler immediately while browser work is in flight.
let editorError;
const completed = editor.catch(e => { editorError = e; });
let browser;
try {
  for (let i = 0; i < 80; i++) {
    if (editorError) throw editorError;
    try { await readFile(resolve(artifacts, 'editor-ready')); break; } catch { await new Promise(r => setTimeout(r, 500)); }
  }
  browser = await chromium.connectOverCDP('http://127.0.0.1:58183');
  const page = browser.contexts()[0].pages().find(p => p.url().includes('workbench'));
  assert.ok(page, 'real VS Code workbench target');
  const messages = [];
  page.on('console', m => messages.push(m.text()));
  page.on('pageerror', e => messages.push(e.message));
  const protocol = await browser.newBrowserCDPSession();
  // The webview process can start a moment after the panel opens (seen in restricted mode), so wait for it.
  let targets, guest;
  for (let i = 0; i < 40 && !guest; i++) {
    targets = await protocol.send('Target.getTargets');
    guest = targets.targetInfos.find(t => t.type === 'iframe' && t.url.startsWith('vscode-webview:'));
    if (!guest) await new Promise(r => setTimeout(r, 250));
  }
  await writeFile(resolve(artifacts, 'editor-targets.json'), JSON.stringify(targets, null, 2));
  assert.ok(guest, 'VS Code out-of-process webview target');
  const { sessionId } = await protocol.send('Target.attachToTarget', { targetId: guest.targetId, flatten: false });
  let messageId = 0;
  const evaluate = expression => new Promise((resolve, reject) => {
    const id = ++messageId;
    const timeout = setTimeout(() => { protocol.off('Target.receivedMessageFromTarget', receive); reject(new Error('Guest evaluation timed out')); }, 5000);
    const receive = e => {
      if (e.sessionId !== sessionId) return;
      const m = JSON.parse(e.message);
      if (m.id !== id) return;
      clearTimeout(timeout); protocol.off('Target.receivedMessageFromTarget', receive);
      if (m.error || m.result.exceptionDetails) reject(new Error(JSON.stringify(m)));
      else resolve(m.result.result.value);
    };
    protocol.on('Target.receivedMessageFromTarget', receive);
    void protocol.send('Target.sendMessageToTarget', { sessionId, message: JSON.stringify({ id, method: 'Runtime.evaluate', params: { expression, returnByValue: true, awaitPromise: true } }) });
  });
  await writeFile(resolve(artifacts, 'guest-body.txt'), await evaluate('document.body.innerText'));
  const doc = `document.querySelector('iframe')?.contentDocument ?? document`;
  let ready = false;
  for (let i = 0; i < 40; i++) {
    ready = await evaluate(`(${doc}).body.innerText.includes('See the query. Understand the evidence.')`);
    if (ready) break;
    await new Promise(r => setTimeout(r, 250));
  }
  let frame = ready;
  if (!frame) {
    await page.screenshot({ path: resolve(artifacts, 'editor-failure.png') });
    await writeFile(resolve(artifacts, 'editor-failure.json'), JSON.stringify({ messages,
      frames: await Promise.all(page.frames().map(async f => ({ url: f.url(), body: (await f.locator('body').innerText()).slice(0, 2000) }))) }, null, 2));
  }
  assert.ok(frame, 'packaged React webview rendered inside actual editor');
  const openMilliseconds = Math.round(performance.now() - startedAt);
  const act = async js => { await evaluate(`(()=>{ const d=${doc}; ${js} })()`); await new Promise(r => setTimeout(r, 100)); };
  const click = name => act(`const b=[...d.querySelectorAll('button')].find(b=>b.textContent===${JSON.stringify(name)}); if(!b) throw new Error('Button missing'); b.click();`);
  const capture = async view => {
    const name = `${screenshotPrefix}-${view}.png`;
    await page.screenshot({ path: resolve(artifacts, name) });
    screenshots.push(name);
  };
  await act(`const s=d.querySelector('select'); s.value='movies.optional.v1'; s.dispatchEvent(new Event('change',{bubbles:true}));`);
  await click('2. OPTIONAL-MATCH');
  assert.match(await evaluate(`(${doc}).querySelector('.scope').innerText`), /Scope before/);
  assert.ok(await evaluate(`(${doc}).querySelectorAll('.selected-line').length`) > 0);
  await capture('query');
  await click('Doctor');
  assert.match(await evaluate(`(${doc}).body.innerText`), /GraphBridge Doctor/);
  await capture('doctor');
  await click('Verify');
  assert.equal(await evaluate(`(${doc}).querySelectorAll('tbody tr').length`), 3);
  await capture('verify');
  await click('movies.shared-movies.v1');
  await click('2. WITH');
  assert.match(await evaluate(`(${doc}).querySelector('.scope').innerText`), /Scope after/);
  await act(`d.querySelector('.scope').scrollIntoView({block:'center'});`);
  const beforeIdle = await protocol.send('SystemInfo.getProcessInfo');
  const heapBefore = await evaluate('performance.memory.usedJSHeapSize');
  await new Promise(r => setTimeout(r, 2000));
  const afterIdle = await protocol.send('SystemInfo.getProcessInfo');
  const heapAfter = await evaluate('performance.memory.usedJSHeapSize');
  const cpu = info => info.processInfo.reduce((sum, p) => sum + p.cpuTime, 0);
  await capture('scope');
  await writeFile(resolve(artifacts, restricted ? 'editor-receipt-untrusted.json' : 'editor-receipt.json'), JSON.stringify({
    status: 'pass', editorExecutable: options.vscodeExecutablePath, installedVsix: 'graphbridge-query-lab-0.1.0.vsix',
    observations: ['actual packaged webview rendered', 'query selection', 'optional clause/Cypher selection', 'Doctor view', 'Verify matrix', 'contract navigation', 'WITH scope'],
    completedAt: new Date().toISOString(), screenshots, restrictedWorkspace: restricted,
    resourceObservation: { openMilliseconds, idleSeconds: 2, webviewProcessHeapBefore: heapBefore,
      webviewProcessHeapAfter: heapAfter, editorCpuSecondsDuringIdle: cpu(afterIdle) - cpu(beforeIdle),
      scope: 'One small three-query report; process heap and whole isolated editor CPU, not a large-graph or cross-platform benchmark.' },
    limitation: 'Offline editor interaction; capture denied in restricted mode. Trusted command flow has separate adapter and live bridge checks.' }, null, 2));
} finally {
  await writeFile(resolve(artifacts, 'editor-finish'), 'done');
  await browser?.close();
  await completed;
}
if (editorError) throw editorError;
assert.ok(await readFile(resolve(artifacts, 'editor-closed')));
console.log('Actual VS Code extension-host and webview interaction checks passed.');
