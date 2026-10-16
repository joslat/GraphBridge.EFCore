const vscode = require('vscode');
const fs = require('node:fs/promises');
const path = require('node:path');
exports.run = async () => {
  const root = process.env.GRAPHBRIDGE_ROOT;
  const artifacts = path.join(root, '.artifacts/vscode');
  const extension = vscode.extensions.getExtension('joslat.graphbridge-query-lab');
  if (!extension) throw new Error('Packaged extension not installed.');
  await extension.activate();
  if (process.env.GRAPHBRIDGE_TEST_UNTRUSTED === '1') {
    if (vscode.workspace.isTrusted) throw new Error('Restricted-mode acceptance requires an actually untrusted workspace.');
    await vscode.commands.executeCommand('graphbridge.captureEvidence');
  }
  await vscode.commands.executeCommand('graphbridge.openEvidence', vscode.Uri.file(path.join(artifacts, 'compiled-evidence.json')));
  await fs.writeFile(path.join(artifacts, 'editor-ready'), extension.extensionPath);
  for (let i = 0; i < 120; i++) {
    try { await fs.access(path.join(artifacts, 'editor-finish')); break; }
    catch { await new Promise(r => setTimeout(r, 500)); }
    if (i === 119) throw new Error('Editor interaction test did not finish.');
  }
  await vscode.commands.executeCommand('workbench.action.closeAllEditors');
  await fs.writeFile(path.join(artifacts, 'editor-closed'), 'Closed all test editors; panel disposal invoked.');
};
