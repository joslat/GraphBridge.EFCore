import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFile, mkdir, symlink, unlink } from 'node:fs/promises';
import { resolve } from 'node:path';
import { capture, parseReply, request, resolveHost } from '../../extensions/graphbridge-query-lab/src/applicationHost';

const root = process.env.GRAPHBRIDGE_ROOT!;
const dll = resolve(root, 'samples/05-FluentCypher/bin/Release/net10.0/FluentCypher.Sample.dll');
const fake = resolve(root, 'tests/GraphBridge.QueryLab.Tests/FakeHost/bin/Release/net10.0/FakeHost.dll');
process.env.GRAPHBRIDGE_FAKE_REPORT = resolve(root, '.artifacts/vscode/compiled-evidence.json');
const signal = () => new AbortController().signal;
test('real application host offline capture respects redacted and disclosed requests', async () => {
  const host = await resolveHost(root, dll);
  for (const disclose of [false, true]) {
    const result = await capture(host, request('offline', disclose), signal());
    assert.equal(result.report.status, 'pass');
    assert.equal(result.evidence!.sensitiveTextIncluded, disclose);
    assert.equal(result.sources.length, disclose ? 3 : 0);
    assert.equal(result.evidence!.databaseObservation, 'not-run');
  }
});
test('selected library/outside-workspace/junction escapes are rejected', async () => {
  await assert.rejects(resolveHost(resolve(root, 'extensions'), dll));
  await assert.rejects(resolveHost(root, resolve(root, 'src/GraphBridge.Tooling/bin/Release/net10.0/GraphBridge.Tooling.dll')));
  const workspace = resolve(root, '.artifacts/vscode/path-test');
  const junction = resolve(workspace, 'linked');
  await mkdir(workspace, { recursive: true });
  await symlink(resolve(root, 'samples/05-FluentCypher/bin/Release/net10.0'), junction, 'junction');
  try { await assert.rejects(resolveHost(workspace, resolve(junction, 'FluentCypher.Sample.dll'))); }
  finally { await unlink(junction); }
});
test('protocol rejects stale requests, changed operations, disclosure and ambiguous result/error', async () => {
  const result = JSON.parse(await readFile(process.env.GRAPHBRIDGE_FAKE_REPORT!, 'utf8'));
  const input = request('offline', true);
  const valid = { formatVersion: 1, requestId: input.requestId, result };
  assert.equal(parseReply(JSON.stringify(valid), input).report.status, 'pass');
  for (const response of [{ ...valid, requestId: '0'.repeat(32) }, { ...valid, formatVersion: 2 },
    { ...valid, error: 'configuration' }, { ...valid, unexpected: true }]) assert.throws(() => parseReply(JSON.stringify(response), input));
  assert.throws(() => parseReply(JSON.stringify(valid), { ...input, operation: 'inspect' }));
  assert.throws(() => parseReply(JSON.stringify(valid), { ...input, includeStatements: false }));
});
for (const mode of ['overflow', 'stderr', 'malformed', 'stale']) test(`bounded host rejects ${mode} without leaking stderr`, async () => {
  process.env.GRAPHBRIDGE_FAKE_MODE = mode;
  await assert.rejects(capture(await resolveHost(root, fake), request('offline', true), signal()), error => {
    assert.ok(error instanceof Error); assert.ok(!error.message.includes('SECRET')); return true;
  });
});
for (const cancel of [false, true]) test(`owned process tree is removed after ${cancel ? 'cancellation' : 'deadline'}`, async () => {
  process.env.GRAPHBRIDGE_FAKE_MODE = 'hang';
  const pidFile = resolve(root, `.artifacts/vscode/child-${cancel}.txt`);
  process.env.GRAPHBRIDGE_FAKE_PID = pidFile;
  const controller = new AbortController();
  const timer = cancel ? setTimeout(() => controller.abort(), 1200) : undefined;
  try { await assert.rejects(capture(await resolveHost(root, fake), request('offline', true), controller.signal, 1800), /cancelled|deadline/); }
  finally { clearTimeout(timer); }
  const pid = Number(await readFile(pidFile, 'utf8'));
  assert.throws(() => process.kill(pid, 0), 'owned descendant no longer exists');
});
test('sample rejects missing acknowledgments, duplicate/unknown fields and configuration before transport', async () => {
  const inputs = [ { ...request('inspect', false), acknowledgeDatabase: false },
    { ...request('semantic', false), acknowledgeDisposable: false }, { ...request('offline', false), extra: 'forbidden' } ];
  for (const input of inputs) {
    const { code, stdout } = await runRaw(JSON.stringify(input));
    assert.ok(code === 2 || JSON.parse(stdout).error === 'invalid-request');
  }
  const input = request('offline', false);
  const duplicate = JSON.stringify(input).replace('{', '{"formatVersion":1,');
  assert.equal((await runRaw(duplicate)).code, 2);
  assert.equal((await runRaw('x'.repeat(4097))).code, 2);
  await assert.rejects(capture(await resolveHost(root, dll), request('semantic', false), signal()), /configuration/);
});
function runRaw(text: string): Promise<{ code: number | null; stdout: string }> {
  return new Promise((done, fail) => {
    const child = spawn('dotnet', [dll, '--lab-host'], { windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'] });
    let stdout = '';
    child.stdout.on('data', b => stdout += b.toString());
    child.once('error', fail); child.once('close', code => done({ code, stdout })); child.stdin.end(text);
  });
}
