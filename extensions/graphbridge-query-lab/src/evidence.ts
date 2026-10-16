import { z } from 'zod';
import { createHash } from 'node:crypto';
// Avoid runtime schema compilation and environment sniffing inside the extension host.
z.config({ jitless: true });

// A projection of the .NET v1 contracts. Unknown fields are stripped, never displayed or executed.
export const MAX_BYTES = 4_000_000;
const text = z.string().max(4096);
const name = z.string().max(512);
const id = z.string().max(100).regex(/^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$/);
const hash = z.string().regex(/^[a-f0-9]{64}$/);
const count = z.number().int().nonnegative().max(Number.MAX_SAFE_INTEGER);
const status = z.enum(['pass', 'fail', 'unsupported', 'blocked', 'not-run']);
export type Status = z.infer<typeof status>;
const diagnostic = z.object({ code: name, status, severity: z.enum(['information', 'warning', 'error']),
  phase: z.enum(['model', 'query', 'schema', 'connection', 'execution']), component: name,
  explanation: text, suggestedAction: text, documentation: text });
const pattern = z.object({ nodes: z.array(z.object({ alias: name, label: name })).max(256),
  steps: z.array(z.object({ alias: name, relationshipType: name, identified: z.boolean(),
    startAlias: name, endAlias: name, direction: z.enum(['outgoing', 'incoming', 'both']),
    mappedSourceAlias: name.optional(), mappedTargetAlias: name.optional(), repeated: z.boolean() })).max(256),
  pathAlias: name.optional(), minimumHops: count.optional(), maximumHops: count.optional() });
const clause = z.object({ kind: z.enum(['match', 'optional-match', 'with', 'return', 'order-by', 'skip', 'limit']),
  firstLine: count.min(1), lastLine: count.min(1), distinct: z.boolean(), hasPredicate: z.boolean(),
  scopeBefore: z.array(name).max(512), scopeAfter: z.array(name).max(512),
  patterns: z.array(pattern).max(256), aggregates: z.array(z.object({ alias: name, expression: text })).max(256) });
const semantics = z.object({ formatVersion: z.literal(1),
  status: z.enum(['complete', 'nested-exists-not-represented', 'budget-exceeded']), clauses: z.array(clause).max(256) });
const query = z.object({ contractId: id, statementFingerprint: hash.optional(),
  statement: z.string().max(65536).optional(), sensitiveTextIncluded: z.boolean(), parameterCount: count.max(4096),
  parameters: z.array(z.object({ position: count, kind: name })).max(4096),
  definition: z.object({ id, description: text, sourceReference: text.optional(), comparison: name,
    duplicates: name, referenceId: id.optional(), fixtureId: id.optional() }).optional(),
  diagnostic: diagnostic.optional(), compilation: status, execution: z.literal('not-run'), semantics: semantics.optional() });
const database = z.object({ profileId: id, serverVersion: name, edition: name,
  schemaStatus: z.enum(['pass', 'warn', 'fail']), status,
  findings: z.array(z.object({ code: name, category: name, status: z.enum(['pass', 'warn', 'fail']), count })).max(1024) });
const evidence = z.object({ formatVersion: z.literal(1), mode: z.enum(['offline', 'inspect', 'semantic']),
  versions: z.object({ provider: name, efCore: name, driver: name, tooling: name }),
  model: z.object({ fingerprint: hash, nodes: count, relationships: count }).optional(), database: database.optional(),
  queries: z.array(query).max(256), diagnostics: z.array(diagnostic).max(1024),
  parameterValuesIncluded: z.literal(false), sensitiveTextIncluded: z.boolean(), databaseObservation: status, status });
const plan = z.object({ statementFingerprint: hash, shapeFingerprint: hash,
  operators: z.array(z.object({ index: count, parent: count.optional(), operator: name })).max(4096),
  argumentsIncluded: z.literal(false), identifiersIncluded: z.literal(false), hasRedactedOperators: z.boolean() });
const contract = z.object({ contractId: id, compilation: status, planning: status,
  semantics: z.literal('not-run'), plan: plan.optional(), diagnostic: diagnostic.optional() });
const verification = z.object({ formatVersion: z.literal(1), tier: z.enum(['v0', 'v1']), evidence,
  contracts: z.array(contract).max(256), requiredContractCount: count.max(256), status, exitCode: z.union([z.literal(0), z.literal(1)]) });
const comparison = z.object({ status, difference: z.enum(['none', 'row-count', 'values', 'order', 'duplicate-rows',
  'invalid-expected-shape', 'invalid-actual-shape', 'budget-exceeded']),
  expectedRows: count.optional(), actualRows: count.optional(), firstDifferentRow: count.optional() });
const semantic = z.object({ formatVersion: z.literal(1), tier: z.literal('V2'), fixtureId: id, fixtureFingerprint: hash,
  inspection: verification.optional(), fixtureStatus: status, cleanup: status, issue: name,
  cases: z.array(z.object({ contractId: id, referenceId: id.optional(), referenceFingerprint: hash.optional(), status,
    referenceComparison: comparison.optional(), queryComparison: comparison.optional(), diagnostic: diagnostic.optional() })).max(256),
  diagnostics: z.array(diagnostic).max(1024), status, exitCode: z.union([z.literal(0), z.literal(1)]) });
const report = z.union([evidence, verification, semantic]);
const envelope = z.object({ kind: z.literal('graphbridge-query-lab'), formatVersion: z.literal(1), report,
  sources: z.array(z.object({ contractId: id, text: z.string().max(32768) })).max(256) });
export type Query = z.infer<typeof query>;
export type Clause = z.infer<typeof clause>;
export type Pattern = z.infer<typeof pattern>;
export type Diagnostic = z.infer<typeof diagnostic>;
export type Comparison = z.infer<typeof comparison>;
export interface SavedEvidence {
  report: z.infer<typeof report>;
  evidence?: z.infer<typeof evidence>;
  verification?: z.infer<typeof verification>;
  semantic?: z.infer<typeof semantic>;
  sources: z.infer<typeof envelope>['sources'];
}

function invariant(condition: unknown): asserts condition {
  if (!condition) throw new Error('Inconsistent evidence. Re-export the report with compatible GraphBridge tooling.');
}
function unique(values: string[]) { invariant(new Set(values).size === values.length); }
const fingerprint = (value: string) => createHash('sha256').update(value, 'utf8').digest('hex');

export function parseEvidence(json: string): SavedEvidence {
  if (new TextEncoder().encode(json).length > MAX_BYTES) throw new Error('Evidence exceeds the 4 MB limit.');
  let raw: unknown;
  try { raw = JSON.parse(json.replace(/^\uFEFF/, '')); }
  catch { throw new Error('Expected a saved JSON evidence report, not an HTML export.'); }
  // Iterative preflight keeps deeply nested or enormous hostile inputs out of recursive schema validation.
  const pending = [{ value: raw, depth: 0 }];
  let budget = 100_000;
  while (pending.length) {
    const { value, depth } = pending.pop()!;
    if (--budget < 0 || depth > 32) throw new Error('Evidence exceeds structural limits.');
    if (value && typeof value === 'object') for (const child of Object.values(value)) pending.push({ value: child, depth: depth + 1 });
  }
  const wrapped = !!raw && typeof raw === 'object' && 'kind' in raw;
  const result = wrapped ? envelope.safeParse(raw) : report.safeParse(raw);
  if (!result.success) throw new Error('Unsupported or malformed evidence. Expected GraphBridge JSON format version 1.');
  const data = result.data;
  const item = 'kind' in data ? data.report : data;
  const sources = 'kind' in data ? data.sources : [];
  const v2 = 'tier' in item && item.tier === 'V2' ? item : undefined;
  const v = 'evidence' in item ? item : v2?.inspection;
  const e = 'queries' in item ? item : v?.evidence;
  unique(sources.map(s => s.contractId));
  invariant(sources.every(s => e?.queries.some(q => q.contractId === s.contractId)));
  if (e) {
    unique(e.queries.map(q => q.contractId));
    invariant(e.sensitiveTextIncluded === e.queries.some(q => q.sensitiveTextIncluded));
    for (const q of e.queries) {
      invariant(q.sensitiveTextIncluded === (q.statement !== undefined));
      invariant(q.statement === undefined || fingerprint(q.statement) === q.statementFingerprint);
      invariant(!q.semantics || q.statement !== undefined);
      invariant(!q.definition || q.definition.id === q.contractId);
      invariant(q.parameterCount === q.parameters.length && q.parameters.every((p, i) => p.position === i));
      if (q.semantics) {
        invariant(q.semantics.status === 'complete' || q.semantics.clauses.length === 0);
        let end = 0;
        for (const c of q.semantics.clauses) {
          invariant(c.firstLine > end && c.lastLine >= c.firstLine && c.lastLine <= q.statement!.split('\n').length);
          end = c.lastLine;
          for (const p of c.patterns) {
            invariant(p.steps.every(s => p.nodes.some(n => n.alias === s.startAlias) && p.nodes.some(n => n.alias === s.endAlias)));
            invariant(p.maximumHops === undefined || p.minimumHops === undefined || p.maximumHops >= p.minimumHops);
          }
        }
      }
    }
  }
  if (v) {
    unique(v.contracts.map(c => c.contractId));
    invariant(v.requiredContractCount === v.contracts.length);
    invariant(v.contracts.length === e!.queries.length);
    for (const c of v.contracts) {
      const q = e!.queries.find(q => q.contractId === c.contractId);
      invariant(q && q.compilation === c.compilation);
      if (c.plan) {
        invariant(c.plan.statementFingerprint === q.statementFingerprint);
        invariant(c.plan.operators.every((n, i) => n.index === i && (i === 0 ? n.parent === undefined : n.parent !== undefined && n.parent < i)));
        invariant(fingerprint(c.plan.operators.map(n => `${n.index}:${n.parent ?? '-'}:${n.operator}`).join('\n')) === c.plan.shapeFingerprint);
      }
    }
  }
  if (v2) unique(v2.cases.map(c => c.contractId));
  return { report: item, evidence: e, verification: v, semantic: v2, sources };
}
