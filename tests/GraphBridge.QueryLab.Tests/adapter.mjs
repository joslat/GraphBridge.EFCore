import { requireMemory } from './runtime.mjs';
requireMemory();
import { build } from '../../extensions/graphbridge-query-lab/node_modules/esbuild/lib/main.js';
import { createRequire } from 'node:module';
import { resolve } from 'node:path';
import assert from 'node:assert/strict';
const root = resolve(import.meta.dirname, '../..');
const output = resolve(root, '.artifacts/vscode/adapter.cjs');
const source = `
class Uri { constructor(path,scheme='file'){this.fsPath=path;this.scheme=scheme;} static joinPath(base,...p){return new Uri(base.fsPath+'/'+p.join('/'));} }
class Disposable { constructor(fn){this.dispose=fn;} }
const registry=new Map(), events=[], panels=[];
const workspace={isTrusted:false,workspaceFolders:[{uri:new Uri(${JSON.stringify(root)})}]};
const window={
 showErrorMessage:async m=>events.push(['error',m]), showInformationMessage:async m=>events.push(['info',m]),
 showOpenDialog:async()=>[new Uri(${JSON.stringify(resolve(root, 'samples/05-FluentCypher/bin/Release/net10.0/FluentCypher.Sample.dll'))})],
 showQuickPick:async items=>items[0], showWarningMessage:async(m,o,action)=>{events.push(['confirm',m]);return action;},
 withProgress:async(o,fn)=>fn({}, {isCancellationRequested:false,onCancellationRequested:()=>new Disposable(()=>{})}),
 createWebviewPanel:()=>{const p={disposed:false,webview:{html:'',cspSource:'vscode-resource:',asWebviewUri:uri=>uri.fsPath,
 onDidReceiveMessage:fn=>{p.receive=fn;return new Disposable(()=>{});},postMessage:async m=>{p.message=m;}},
 reveal:()=>{},onDidDispose:fn=>{p.onDispose=fn;},dispose:()=>{p.disposed=true;p.onDispose?.();}};panels.push(p);return p;}
};
module.exports={Uri,Disposable,workspace,window,ViewColumn:{Active:1},ProgressLocation:{Notification:1},
 commands:{registerCommand:(id,fn)=>{registry.set(id,fn);return new Disposable(()=>{});}},registry,events,panels};`;
await build({ entryPoints: [resolve(root, 'extensions/graphbridge-query-lab/src/extension.ts')], outfile: output, bundle: true, platform: 'node', format: 'cjs',
  plugins: [{ name: 'private-vscode-adapter', setup(b) {
    b.onResolve({ filter: /^vscode$/ }, () => ({ path: 'vscode', namespace: 'mock' }));
    b.onLoad({ filter: /.*/, namespace: 'mock' }, () => ({ contents: source + '\nglobalThis.__vscodeMock=module.exports;', loader: 'js' }));
  } }] });
const { activate } = createRequire(import.meta.url)(output);
const api = globalThis.__vscodeMock;
const context = { extensionUri: new api.Uri(resolve(root, 'extensions/graphbridge-query-lab')), subscriptions: [] };
activate(context);
await api.registry.get('graphbridge.captureEvidence')();
assert.equal(api.panels.length, 0);
assert.ok(api.events.some(e => e[1].includes('trusted workspace')));
api.workspace.isTrusted = true;
const consent = api.window.showWarningMessage;
api.window.showWarningMessage = async () => undefined;
await api.registry.get('graphbridge.captureEvidence')();
assert.equal(api.panels.length, 0, 'declined execution never opens a capture');
api.window.showWarningMessage = consent;
await api.registry.get('graphbridge.captureEvidence')();
assert.equal(api.panels.length, 1);
const panel = api.panels[0];
assert.match(panel.webview.html, /default-src 'none'/);
assert.match(panel.webview.html, /connect-src 'none'/);
assert.doesNotMatch(panel.webview.html, /unsafe-inline|unsafe-eval/);
await panel.receive({ type: 'ready' });
assert.equal(panel.message.data.report.status, 'pass');
assert.equal(panel.message.data.sources.length, 0);
assert.equal(panel.message.data.evidence.sensitiveTextIncluded, false);
const confirmations = api.events.length;
await panel.receive({ type: 'capture', operation: 'semantic' });
assert.equal(api.events.length, confirmations, 'webview messages cannot trigger host operations');
await api.registry.get('graphbridge.openEvidence')(new api.Uri(resolve(root, '.artifacts/vscode/compiled-evidence.json')));
assert.equal(api.panels.length, 1, 'reuse one panel');
for (const d of context.subscriptions) d.dispose();
assert.equal(panel.disposed, true);
console.log('Adapter checks pass: trust, declined consent, actual offline capture, no unsolicited disclosure, CSP, hostile message rejection, panel reuse/disposal.');
