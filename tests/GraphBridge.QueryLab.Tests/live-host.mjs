import { build } from '../../extensions/graphbridge-query-lab/node_modules/esbuild/lib/main.js';
import { createRequire } from 'node:module';
import { resolve } from 'node:path';
import { writeFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
const root = resolve(import.meta.dirname, '../..');
const output = resolve(root, '.artifacts/vscode/live-host.cjs');
await build({ entryPoints: [resolve(root, 'extensions/graphbridge-query-lab/src/applicationHost.ts')], outfile: output,
  bundle: true, platform: 'node', format: 'cjs' });
const { capture, request, resolveHost } = createRequire(import.meta.url)(output);
const host = await resolveHost(root, 'samples/05-FluentCypher/bin/Debug/net10.0/FluentCypher.Sample.dll');
const results = [];
for (const operation of ['inspect', 'semantic']) {
  const result = await capture(host, request(operation, true), new AbortController().signal);
  assert.equal(result.report.status, 'pass');
  assert.equal(result.verification.contracts.length, 3);
  assert.ok(result.verification.contracts.every(c => c.plan && c.planning === 'pass'));
  if (operation === 'semantic') {
    assert.ok(result.semantic.cases.every(c => c.referenceComparison.status === 'pass' && c.queryComparison.status === 'pass'));
    assert.equal(result.semantic.cleanup, 'pass');
  }
  await writeFile(resolve(root, `.artifacts/vscode/host-${operation}.json`), JSON.stringify(result, null, 2));
  results.push({ operation, status: result.report.status, queries: 3 });
}
// A valid disposable acknowledgment is still not ownership evidence: a different registered run is rejected.
process.env.GRAPHBRIDGE_VERIFY_RUN_ID = '0123456789abcdef0123456789abcdef';
const wrong = await capture(host, request('semantic', false), new AbortController().signal);
assert.notEqual(wrong.report.status, 'pass');
assert.ok(wrong.semantic.cases.every(c => !c.queryComparison || c.queryComparison.status !== 'pass'));
await writeFile(resolve(root, '.artifacts/vscode/live-host-receipt.json'), JSON.stringify({ status: 'pass', results,
  mismatchedRun: wrong.report.status, scope: 'Owned disposable Community fixture. Graph/schema preservation checked by outer .NET harness.' }, null, 2));
console.log('Live bridge assertions passed');
