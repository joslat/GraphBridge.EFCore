import { useId, useState } from 'react';
import type { Clause, Comparison, Diagnostic, Pattern, Query, SavedEvidence, Status } from '../evidence';

const symbols: Record<Status, string> = { pass: '✓', fail: '×', blocked: '!', unsupported: '—', 'not-run': '○' };
export function Badge({ value }: { value: Status }) {
  return <span className={`badge ${value}`}>{symbols[value]} {value}</span>;
}
export function App({ saved }: { saved: SavedEvidence }) {
  const [view, setView] = useState<'Query Lab' | 'Doctor' | 'Verify'>('Query Lab');
  const queries = saved.evidence?.queries ?? [];
  const [selected, setSelected] = useState(queries[0]?.contractId ?? '');
  const q = queries.find(q => q.contractId === selected);
  const select = (id: string) => { setSelected(id); setView('Query Lab'); };
  return <main>
    <header><div className="eyebrow">GRAPHBRIDGE · SAVED EVIDENCE</div><h1>See the query. Understand the evidence.</h1>
      <p>Query patterns, diagnostics and verification in one place.</p><Badge value={saved.report.status} />
      <span className="tag">{'tier' in saved.report ? `Verify ${saved.report.tier.toUpperCase()}` : `Doctor ${saved.report.mode}`}</span>
    </header>
    <aside className="notice">Saved observations, not a live connection or compatibility certificate. Parameter values are excluded.
      {saved.evidence?.sensitiveTextIncluded ? ' This report discloses statement and mapped schema text.' : ' Statement text is redacted.'}
      {saved.sources.length > 0 && ' Source excerpts are application-declared, not verified source maps.'}</aside>
    <nav aria-label="Evidence views">{(['Query Lab', 'Doctor', 'Verify'] as const).map(tab =>
      <button key={tab} aria-pressed={view === tab} onClick={() => setView(tab)}>{tab}</button>)}</nav>
    {view === 'Query Lab' && <section aria-label="Query Lab">
      <label className="picker">Query contract<select value={selected} onChange={e => setSelected(e.target.value)}>
        {queries.map(q => <option key={q.contractId} value={q.contractId}>{q.contractId}</option>)}
      </select></label>
      {q ? <QueryView key={q.contractId} query={q} saved={saved} /> : <p>No compiled query evidence is available in this report.</p>}
    </section>}
    {view === 'Doctor' && <Doctor saved={saved} select={select} />}
    {view === 'Verify' && <Verify saved={saved} select={select} />}
    <details className="versions"><summary>Evidence provenance and versions</summary>
      <p>File-provided observations are not digitally signed. Fingerprints identify captured content; they do not prove its origin.</p>
      {saved.evidence && <dl>{Object.entries(saved.evidence.versions).map(([k, v]) => <div key={k}><dt>{k}</dt><dd>{v}</dd></div>)}</dl>}
      {saved.evidence?.model && <p>Model: {saved.evidence.model.nodes} node mappings · {saved.evidence.model.relationships} relationship mappings<br />{saved.evidence.model.fingerprint}</p>}
    </details>
  </main>;
}

function QueryView({ query: q, saved }: { query: Query; saved: SavedEvidence }) {
  const [index, setIndex] = useState(0);
  const clauses = q.semantics?.clauses ?? [];
  const active = clauses[index];
  const source = saved.sources.find(s => s.contractId === q.contractId);
  const contract = saved.verification?.contracts.find(c => c.contractId === q.contractId);
  return <>
    <h2>{q.contractId}</h2><p>{q.definition?.description}</p>
    <div className="metrics"><article><h3>Compilation</h3><Badge value={q.compilation} /></article>
      <article><h3>Planning</h3><Badge value={contract?.planning ?? 'not-run'} /></article>
      <article><h3>Parameters</h3><strong>{q.parameterCount}</strong><p>Types only · no values</p></article></div>
    {q.diagnostic && <Finding finding={q.diagnostic} />}
    <div className="split"><section className="card"><h3>C# source excerpt</h3>
      {source ? <pre><code>{source.text}</code></pre> : <p>No source excerpt supplied.</p>}
      {q.definition?.sourceReference && <p className="muted">Declared reference: {q.definition.sourceReference}</p>}
      <p className="muted">No inferred C# line mapping or automatic file navigation.</p></section>
      <section className="card"><h3>Generated Cypher</h3>{q.statement !== undefined ?
        <pre className="cypher"><code>{q.statement.split('\n').map((line, i) => <span key={i} className={active && i + 1 >= active.firstLine && i + 1 <= active.lastLine ? 'selected-line' : ''}><span className="line-number" aria-hidden="true">{i + 1}</span>{line}{'\n'}</span>)}</code></pre>
        : <p>Statement redacted. Re-export with explicit statement disclosure to inspect clauses.</p>}</section></div>
    <section className="card"><h3>Compiler clauses and scope</h3>
      {clauses.length > 0 ? <><div className="clauses" aria-label="Select compiler clause">{clauses.map((c, i) => <button key={i} aria-pressed={index === i} onClick={() => setIndex(i)}>{i + 1}. {c.kind.toUpperCase()}</button>)}</div>
        {active && <ClauseView clause={active} />}</> : <p>{q.semantics ? `Typed snapshot: ${q.semantics.status}. No partial graph is inferred.` : 'Typed compiler snapshot unavailable. No graph is inferred from text.'}</p>}
    </section>
    <section className="card"><h3>Captured EXPLAIN plan</h3><p className="muted">Operator structure only. EXPLAIN is not result execution or a performance measurement.</p>
      {contract?.plan ? <ol className="plan">{contract.plan.operators.map(n => <li key={n.index}><strong>#{n.index} {n.operator}</strong> <span className="muted">{n.parent === undefined ? 'root' : `parent #${n.parent}`}</span></li>)}</ol> : <p>No plan captured.</p>}
    </section>
    <details><summary>Parameter kinds and statement fingerprint</summary><p>{q.statementFingerprint ?? 'No compiled statement fingerprint.'}</p>
      <ul>{q.parameters.map(p => <li key={p.position}>Position {p.position}: {p.kind}</li>)}</ul></details>
  </>;
}
function ClauseView({ clause: c }: { clause: Clause }) {
  return <><p>Cypher lines {c.firstLine}–{c.lastLine}{c.hasPredicate && ' · WHERE attached to this clause'}{c.distinct && ' · DISTINCT'}</p>
    <div className="split scope"><div><strong>Scope before</strong><p>{c.scopeBefore.join(', ') || '(empty)'}</p></div><div><strong>Scope after</strong><p>{c.scopeAfter.join(', ') || '(empty)'}</p></div></div>
    {c.aggregates.map((a, i) => <p key={i}><code>{a.alias} ← {a.expression}</code></p>)}
    <p className="muted">Nodes and edges below are query variables and pattern occurrences, not observed database results. The same alias retains its identity.</p>
    {c.patterns.length === 0 && <p>No graph pattern in this clause.</p>}
    {c.patterns.map((p, i) => <PatternView key={i} pattern={p} ordinal={i + 1} />)}
  </>;
}
function PatternView({ pattern: p, ordinal }: { pattern: Pattern; ordinal: number }) {
  const marker = useId().replace(/:/g, '');
  // Render each occurrence in traversal order. Text remains available for dense patterns.
  const visible = p.nodes.length <= 32 && p.steps.length <= 32;
  const nodes = [...new Map(p.nodes.map(n => [n.alias, n])).values()];
  const positions = new Map(nodes.map((n, i) => [n.alias, 100 + i * 240]));
  const width = Math.max(360, nodes.length * 240);
  return <section className="pattern"><h4>Pattern {ordinal}{p.pathAlias && ` · path ${p.pathAlias}`}</h4>
    {p.minimumHops !== undefined && <p>Hops: {p.minimumHops}–{p.maximumHops ?? 'unbounded'}</p>}
    {visible ? <div className="graph-scroll" tabIndex={0} role="region" aria-label={`Pattern ${ordinal} graph, text description follows`}>
      <svg width={width} height="210" role="img" aria-label={`Pattern ${ordinal}: ${p.nodes.length} node occurrences, ${p.steps.length} relationship steps`}>
        <defs><marker id={marker} viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" /></marker></defs>
        {p.steps.map((s, i) => { const x1 = positions.get(s.startAlias)!; const x2 = positions.get(s.endAlias)!;
          return <g key={i}><path className="edge" d={x1 === x2 ? `M ${x1 - 25} 80 C ${x1 - 120} 0 ${x1 + 120} 0 ${x1 + 25} 80` : `M ${x1 + 53} 100 Q ${(x1 + x2) / 2} ${35 - i * 8} ${x2 - 53} 100`}
            markerEnd={s.direction === 'outgoing' ? `url(#${marker})` : undefined} markerStart={s.direction === 'incoming' ? `url(#${marker})` : undefined} />
            <text x={(x1 + x2) / 2} y={40 + i * 17} textAnchor="middle">{s.alias}: {s.relationshipType}{s.repeated ? ' *' : ''}</text></g>; })}
        {nodes.map((n, i) => <g key={i}><circle cx={100 + i * 240} cy="105" r="48" /><text x={100 + i * 240} y="105" textAnchor="middle">{n.alias}</text><text x={100 + i * 240} y="174" textAnchor="middle">:{n.label}</text></g>)}
      </svg></div> : <p>Dense pattern: use the complete text description below (SVG limited to 32 nodes/steps).</p>}
    <details open={!visible}><summary>Accessible pattern description</summary><ul>
      {p.nodes.map((n, i) => <li key={`n${i}`}>Node {n.alias}, label {n.label}</li>)}
      {p.steps.map((s, i) => <li key={`s${i}`}>{s.startAlias} {s.direction === 'incoming' ? '←' : s.direction === 'outgoing' ? '→' : '↔'} {s.endAlias}: {s.alias}, type {s.relationshipType}; {s.identified ? 'identified relationship' : 'relationship without mapped identity'}{s.repeated ? '; repeated path step' : ''}. Physical endpoints: {s.mappedSourceAlias && s.mappedTargetAlias ? `${s.mappedSourceAlias} → ${s.mappedTargetAlias}` : 'not represented for this traversal'}.</li>)}
    </ul></details>
  </section>;
}

function Finding({ finding: d }: { finding: Diagnostic }) {
  return <article className="finding"><h3>{d.code} <Badge value={d.status} /></h3><p className="muted">{d.severity} · {d.phase} · {d.component}</p>
    <p>{d.explanation}</p><p><strong>Suggested action:</strong> {d.suggestedAction}</p><p className="muted">Documentation reference: {d.documentation}</p></article>;
}
function Doctor({ saved, select }: { saved: SavedEvidence; select: (id: string) => void }) {
  const [filter, setFilter] = useState('');
  const [severity, setSeverity] = useState('all');
  const entries: { finding: Diagnostic; query?: string }[] = [];
  const add = (finding: Diagnostic | undefined, query?: string) => {
    if (finding && !entries.some(e => e.query === query && JSON.stringify(e.finding) === JSON.stringify(finding))) entries.push({ finding, query });
  };
  saved.evidence?.diagnostics.forEach(d => add(d));
  saved.semantic?.diagnostics.forEach(d => add(d));
  saved.evidence?.queries.forEach(q => add(q.diagnostic, q.contractId));
  saved.verification?.contracts.forEach(c => add(c.diagnostic, c.contractId));
  saved.semantic?.cases.forEach(c => add(c.diagnostic, c.contractId));
  const matches = (s: string) => s.toLowerCase().includes(filter.toLowerCase());
  const filtered = entries.filter(e => (severity === 'all' || e.finding.severity === severity) && matches(`${e.query} ${e.finding.code} ${e.finding.explanation} ${e.finding.suggestedAction}`));
  const db = saved.evidence?.database;
  return <section><h2>GraphBridge Doctor</h2><p>Captured model, query, connection and schema observations. Suggested actions are guidance; this view executes no repairs.</p>
    <div className="filters"><label>Find findings<input type="search" value={filter} onChange={e => setFilter(e.target.value)} /></label>
      <label>Diagnostic severity<select value={severity} onChange={e => setSeverity(e.target.value)}>{['all', 'error', 'warning', 'information'].map(s => <option key={s}>{s}</option>)}</select></label></div>
    <p role="status">{filtered.length} of {entries.length} diagnostics shown</p>
    {filtered.map((e, i) => <div key={i}><Finding finding={e.finding} />{e.query && saved.evidence?.queries.some(q => q.contractId === e.query) && <button onClick={() => select(e.query!)}>Inspect {e.query}</button>}</div>)}
    {entries.length === 0 && <p>No diagnostic entries retained. This alone does not imply verification passed.</p>}
    <h3>Schema observations</h3>{db ? <><p>{db.profileId} · {db.edition} {db.serverVersion} · schema {db.schemaStatus} <Badge value={db.status} /></p>
      <div className="table-scroll"><table><thead><tr><th>Finding</th><th>Category</th><th>Schema status</th><th>Count</th></tr></thead><tbody>{db.findings.filter(f => matches(`${f.code} ${f.category}`)).map((f, i) => <tr key={i}><th>{f.code}</th><td>{f.category}</td><td>{f.status}</td><td>{f.count}</td></tr>)}</tbody></table></div>
      <p className="muted">Schema findings are aggregated by the .NET inspector. Object names, repair DDL and raw exception text are excluded.</p></> : <p>Database schema was not observed.</p>}
  </section>;
}
function ComparisonView({ value }: { value?: Comparison }) {
  return value ? <><Badge value={value.status} /><p>{value.difference}</p><small>Expected rows: {value.expectedRows ?? 'unknown'} · observed: {value.actualRows ?? 'unknown'}{value.firstDifferentRow !== undefined && ` · first difference at zero-based row ${value.firstDifferentRow}`}</small></> : <Badge value="not-run" />;
}
function Verify({ saved, select }: { saved: SavedEvidence; select: (id: string) => void }) {
  const cases = saved.semantic?.cases ?? [];
  const contracts = saved.verification?.contracts ?? [];
  const ids = [...new Set([...contracts.map(c => c.contractId), ...cases.map(c => c.contractId)])];
  return <section><h2>GraphBridge Verify</h2><p>Compilation, EXPLAIN planning and V2 result comparisons remain separate. An absent observation is not-run.</p>
    {saved.semantic && <div className="metrics"><article><h3>Owned fixture observation</h3><Badge value={saved.semantic.fixtureStatus} /><p>{saved.semantic.fixtureId}</p></article>
      <article><h3>Transaction cleanup</h3><Badge value={saved.semantic.cleanup} /><p>Not fixture teardown evidence.</p></article><article><h3>Issue</h3><p>{saved.semantic.issue}</p></article></div>}
    {ids.length ? <div className="table-scroll"><table><caption>Required query contracts and saved outcomes</caption><thead><tr><th>Contract</th><th>Compilation</th><th>Planning</th><th>V2 status</th><th>Native vs fixed expectation</th><th>Fluent vs native</th></tr></thead>
      <tbody>{ids.map(id => { const c = contracts.find(c => c.contractId === id); const s = cases.find(c => c.contractId === id); return <tr key={id}>
        <th>{saved.evidence?.queries.some(q => q.contractId === id) ? <button onClick={() => select(id)}>{id}</button> : id}<small>{s?.referenceId}</small></th>
        <td><Badge value={c?.compilation ?? 'not-run'} /></td><td><Badge value={c?.planning ?? 'not-run'} /></td><td><Badge value={s?.status ?? 'not-run'} /></td>
        <td><ComparisonView value={s?.referenceComparison} /></td><td><ComparisonView value={s?.queryComparison} /></td></tr>; })}</tbody></table></div>
      : <p>No Verify contract results in this report. Open a saved V0, V1 or V2 report.</p>}
    <p className="muted">No raw result values or fixture ownership tokens are displayed. Native-reference correctness is checked against fixed expectations independently of the Fluent comparison.</p>
  </section>;
}
