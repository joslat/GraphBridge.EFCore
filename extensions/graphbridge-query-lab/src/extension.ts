import * as vscode from 'vscode';
import { randomBytes } from 'node:crypto';
import { open } from 'node:fs/promises';
import { MAX_BYTES, parseEvidence, type SavedEvidence } from './evidence';
import { capture, request, resolveHost, type Operation } from './applicationHost';

// File bytes and webview messages never become commands, URLs, HTML or executable query text.
export async function readEvidence(path: string): Promise<SavedEvidence> {
  const file = await open(path, 'r');
  try {
    const stat = await file.stat();
    if (!stat.isFile() || stat.size > MAX_BYTES) throw new Error('Select a regular JSON evidence file of at most 4 MB.');
    const bytes = Buffer.alloc(MAX_BYTES + 1);
    let length = 0;
    while (length < bytes.length) {
      const { bytesRead } = await file.read(bytes, length, bytes.length - length, null);
      if (bytesRead === 0) break;
      length += bytesRead;
    }
    if (length > MAX_BYTES) throw new Error('Evidence exceeds the 4 MB limit.');
    return parseEvidence(new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(0, length)));
  } finally { await file.close(); }
}

export function activate(context: vscode.ExtensionContext) {
  let panel: vscode.WebviewPanel | undefined;
  let saved: SavedEvidence | undefined;
  let opening = false;
  let activeCapture: AbortController | undefined;
  const show = async (value: SavedEvidence) => {
      saved = value;
      if (panel) { panel.reveal(); await panel.webview.postMessage({ type: 'evidence', data: saved }); return; }
      const assets = vscode.Uri.joinPath(context.extensionUri, 'dist');
      panel = vscode.window.createWebviewPanel('graphbridge.queryLab', 'GraphBridge Query Lab', vscode.ViewColumn.Active,
        { enableScripts: true, enableCommandUris: false, localResourceRoots: [assets], retainContextWhenHidden: false });
      const current = panel;
      const listener = current.webview.onDidReceiveMessage((message: unknown) => {
        if (message && typeof message === 'object' && Object.keys(message).length === 1 &&
            'type' in message && message.type === 'ready' && saved) {
          void current.webview.postMessage({ type: 'evidence', data: saved });
        }
      });
      current.onDidDispose(() => { activeCapture?.abort(); listener.dispose(); panel = undefined; saved = undefined; });
      const nonce = randomBytes(24).toString('base64');
      const script = current.webview.asWebviewUri(vscode.Uri.joinPath(assets, 'webview.js'));
      const style = current.webview.asWebviewUri(vscode.Uri.joinPath(assets, 'webview.css'));
      current.webview.html = `<!doctype html><html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'nonce-${nonce}'; style-src ${current.webview.cspSource}; connect-src 'none'; img-src 'none'; font-src 'none'; base-uri 'none'; form-action 'none';">
        <link rel="stylesheet" href="${style}"><title>GraphBridge Query Lab</title></head>
        <body><div id="root"></div><script nonce="${nonce}" src="${script}"></script></body></html>`;
  };
  context.subscriptions.push(vscode.commands.registerCommand('graphbridge.openEvidence', async (selected?: vscode.Uri) => {
    if (opening) return;
    opening = true;
    try {
      const files = selected instanceof vscode.Uri ? [selected] : await vscode.window.showOpenDialog({ canSelectMany: false, filters: { 'GraphBridge evidence': ['json'] },
        title: 'Open captured GraphBridge evidence' });
      const uri = files?.[0];
      if (!uri) return;
      if (uri.scheme !== 'file') throw new Error('This preview supports local saved evidence files only.');
      await show(await readEvidence(uri.fsPath));
    } catch (error) {
      // Never disclose raw file content or parser exception values in notifications.
      const known = error instanceof Error && /^(Evidence|Select a regular|Expected a saved|Unsupported or malformed|Inconsistent evidence|This preview)/.test(error.message);
      void vscode.window.showErrorMessage(known ? error.message : 'Unable to read GraphBridge evidence. Check the selected local file and its encoding.');
    } finally { opening = false; }
  }), vscode.commands.registerCommand('graphbridge.captureEvidence', async () => {
    if (!vscode.workspace.isTrusted) { void vscode.window.showErrorMessage('Host capture requires a trusted workspace. Saved evidence can still be opened.'); return; }
    if (activeCapture) { void vscode.window.showInformationMessage('A GraphBridge capture is already running.'); return; }
    const controller = new AbortController();
    activeCapture = controller;
    try {
      const folders = vscode.workspace.workspaceFolders?.filter(f => f.uri.scheme === 'file') ?? [];
      const folder = folders.length === 1 ? folders[0] : await vscode.window.showWorkspaceFolderPick({ placeHolder: 'Select the trusted local application workspace' });
      if (!folder || folder.uri.scheme !== 'file') return;
      const files = await vscode.window.showOpenDialog({ defaultUri: folder.uri, canSelectMany: false, filters: { '.NET application DLL': ['dll'] },
        title: 'Select a reviewed, prebuilt application host implementing --lab-host' });
      if (!files?.[0] || files[0].scheme !== 'file') return;
      const host = await resolveHost(folder.uri.fsPath, files[0].fsPath);
      const operation = await vscode.window.showQuickPick([
        { label: 'Compile offline (V0)', value: 'offline' as Operation, description: 'Registered queries; no configured database connection' },
        { label: 'Inspect and EXPLAIN (V1)', value: 'inspect' as Operation, description: 'Connects to the application-configured database' },
        { label: 'Verify disposable fixture (V2)', value: 'semantic' as Operation, description: 'Executes registered reads and native controls on an owned disposable fixture' }
      ], { title: 'GraphBridge capture operation' });
      if (!operation) return;
      const disclosure = await vscode.window.showQuickPick([
        { label: 'Redacted evidence', value: false, description: 'No statement text, typed patterns or source excerpts' },
        { label: 'Include statements and source', value: true, description: 'Discloses mapped schema names, generated Cypher and declared C# excerpts' }
      ], { title: 'Evidence disclosure' });
      if (!disclosure) return;
      const action = operation.value === 'offline' ? 'Run offline capture' : operation.value === 'inspect' ? 'Connect and inspect' : 'Verify owned disposable fixture';
      const confirm = await vscode.window.showWarningMessage(
        `Run trusted application code from ${host.dll}? ${operation.value === 'offline' ? 'The protocol requests offline compilation.' : operation.value === 'inspect' ? 'This requests database metadata and EXPLAIN plans.' : 'Confirm this is an exclusively owned disposable fixture; registered queries and native controls will execute.'} Application code owns its configuration; this is not a sandbox.`,
        { modal: true }, action);
      if (confirm !== action || !vscode.workspace.isTrusted) return;
      // Re-resolve after user review; never rely on a path cached before the dialogs.
      const checked = await resolveHost(folder.uri.fsPath, host.dll);
      await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'GraphBridge: capturing evidence', cancellable: true }, async (_, token) => {
        const cancellation = token.onCancellationRequested(() => controller.abort());
        try {
          if (token.isCancellationRequested) controller.abort();
          await show(await capture(checked, request(operation.value, disclosure.value), controller.signal));
        } finally { cancellation.dispose(); }
      });
    } catch (error) {
      // Our host boundary creates fixed messages; filesystem/process diagnostics can contain sensitive paths.
      const message = error instanceof Error && /^(Host |Capture cancelled|Select a |The application runtime|Unable to start)/.test(error.message)
        ? error.message : 'Unable to capture evidence. Check the reviewed application host and workspace configuration.';
      void vscode.window.showErrorMessage(message);
    } finally { if (activeCapture === controller) activeCapture = undefined; }
  }), new vscode.Disposable(() => { activeCapture?.abort(); panel?.dispose(); saved = undefined; }));
}
