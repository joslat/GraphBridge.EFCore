import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { parseEvidence, MAX_BYTES } from '../../extensions/graphbridge-query-lab/src/evidence';
import { renderToStaticMarkup } from '../../extensions/graphbridge-query-lab/node_modules/react-dom/server';
import { App } from '../../extensions/graphbridge-query-lab/src/ui/views';
import { createElement } from '../../extensions/graphbridge-query-lab/node_modules/react';

const root = process.env.GRAPHBRIDGE_ROOT!;
const real = () => JSON.parse(readFileSync(resolve(root, '.artifacts/vscode/compiled-evidence.json'), 'utf8'));
const parse = (o: unknown) => parseEvidence(JSON.stringify(o));
for (const name of ['doctor-offline', 'doctor-connected', 'verify-offline', 'verify-connected', 'verify-missing-schema', 'verify-semantic']) {
  test(`accept actual .NET ${name} report without inventing execution`, () => {
    const saved = parseEvidence(readFileSync(resolve(root, `tests/GraphBridge.QueryLab.Tests/fixtures/${name}.json`), 'utf8'));
    assert.ok(saved.evidence);
    assert.ok(saved.evidence.queries.every(q => q.execution === 'not-run'));
    if (name === 'verify-semantic') assert.ok(saved.semantic?.cases.some(c => c.queryComparison?.status === 'pass'));
  });
}
test('real compiled clauses, sources and optional scope survive projection', () => {
  const saved = parse(real());
  assert.equal(saved.sources.length, 3);
  const optional = saved.evidence!.queries.find(q => q.contractId === 'movies.optional.v1')!;
  assert.ok(optional.semantics!.clauses.some(c => c.kind === 'optional-match' && c.hasPredicate));
  const html = renderToStaticMarkup(createElement(App, { saved }));
  assert.match(html, /See the query/);
  assert.match(html, /Compiler clauses and scope/);
  assert.match(html, /<svg/);
});
test('unknown properties including parameter values never reach the webview', () => {
  const raw = real(); raw.report.evidence.queries[0].parameters[0].value = 'DO-NOT-DISCLOSE';
  raw.report.credentials = { password: 'DO-NOT-DISCLOSE' };
  assert.ok(!JSON.stringify(parse(raw)).includes('DO-NOT-DISCLOSE'));
});
for (const mutate of [
  (r: any) => { r.formatVersion = 2; },
  (r: any) => { r.report.evidence.formatVersion = 2; },
  (r: any) => { r.report.evidence.parameterValuesIncluded = true; },
  (r: any) => { r.report.evidence.queries.push(r.report.evidence.queries[0]); },
  (r: any) => { r.sources[0].contractId = 'missing.contract'; },
  (r: any) => { r.sources.push(r.sources[0]); },
  (r: any) => { r.report.evidence.queries[0].sensitiveTextIncluded = false; },
  (r: any) => { r.report.evidence.queries[0].semantics.formatVersion = 2; },
  (r: any) => { r.report.evidence.queries[0].semantics.status = 'budget-exceeded'; },
  (r: any) => { r.report.evidence.queries[0].semantics.clauses[0].lastLine = 99999; },
  (r: any) => { r.report.contracts[0].contractId = 'missing.contract'; },
  (r: any) => { r.report.evidence.queries[0].parameters[0].position = 9; },
  (r: any) => { r.report.evidence.queries[0].statement += '\nRETURN 1'; },
]) test(`reject inconsistent input ${mutate.toString()}`, () => { const raw = real(); mutate(raw); assert.throws(() => parse(raw)); });
test('redacted reports do not invent graph or source', () => {
  const saved = parseEvidence(readFileSync(resolve(root, 'tests/GraphBridge.QueryLab.Tests/fixtures/verify-offline.json'), 'utf8'));
  const html = renderToStaticMarkup(createElement(App, { saved }));
  assert.match(html, /Statement redacted/); assert.doesNotMatch(html, /<svg/);
});
test('HTML and command text remains escaped inert text', () => {
  const raw = real(); raw.sources[0].text = '<script>alert(1)</script><a href="command:evil">X</a>';
  const html = renderToStaticMarkup(createElement(App, { saved: parse(raw) }));
  assert.match(html, /&lt;script&gt;/); assert.doesNotMatch(html, /<script>|href="command:/);
});
test('reject oversized, malformed, HTML and deep inputs before schema recursion', () => {
  for (const raw of ['x'.repeat(MAX_BYTES + 1), '{', '<html>Hi</html>', '['.repeat(40) + '0' + ']'.repeat(40)]) assert.throws(() => parseEvidence(raw));
});
test('captured plan must identify the matching query', () => {
  const raw = JSON.parse(readFileSync(resolve(root, 'tests/GraphBridge.QueryLab.Tests/fixtures/verify-connected.json'), 'utf8'));
  assert.ok(raw.contracts[0].plan);
  raw.contracts[0].plan.statementFingerprint = '0'.repeat(64);
  assert.throws(() => parse(raw));
});
test('a changed captured plan shape cannot retain its old fingerprint', () => {
  const raw = JSON.parse(readFileSync(resolve(root, 'tests/GraphBridge.QueryLab.Tests/fixtures/verify-connected.json'), 'utf8'));
  raw.contracts[0].plan.operators[0].operator = 'ChangedOperator';
  assert.throws(() => parse(raw));
});
for (const mode of ['inspect', 'semantic']) test(`retained synthetic live bridge ${mode} fixture passes statement and plan integrity checks`, () => {
  const captured = JSON.parse(readFileSync(resolve(root, `tests/GraphBridge.QueryLab.Tests/fixtures/host-${mode}.json`), 'utf8'));
  const saved = parse({ kind: 'graphbridge-query-lab', formatVersion: 1, report: captured.report, sources: captured.sources });
  assert.equal(saved.report.status, 'pass');
  assert.equal(saved.verification!.contracts.length, 3);
});
