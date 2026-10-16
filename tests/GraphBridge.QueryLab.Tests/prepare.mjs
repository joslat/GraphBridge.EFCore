import { mkdir, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import { root, requireMemory } from './runtime.mjs';

const run = (args) => {
  requireMemory();
  const result = spawnSync('dotnet', args, { cwd: root, stdio: 'inherit', windowsHide: true });
  if (result.error || result.status !== 0) throw result.error ?? new Error(`dotnet failed (${result.status})`);
};
for (const project of ['samples/05-FluentCypher/FluentCypher.Sample.csproj', 'tests/GraphBridge.QueryLab.Tests/FakeHost/FakeHost.csproj']) {
  run(['restore', project, '--locked-mode', '--disable-parallel']);
  run(['build', project, '--no-restore', '-c', 'Release', '-m:1', '-p:UseSharedCompilation=false', '-nodeReuse:false']);
}
await mkdir(resolve(root, '.artifacts/vscode'), { recursive: true });
requireMemory();
const result = spawnSync('dotnet', [resolve(root, 'samples/05-FluentCypher/bin/Release/net10.0/FluentCypher.Sample.dll'), '--lab-json'],
  { cwd: root, encoding: 'utf8', maxBuffer: 4_000_000, windowsHide: true });
if (result.error || result.status !== 0) throw result.error ?? new Error('Offline export failed.');
const data = JSON.parse(result.stdout);
if (data.report.status !== 'pass') throw new Error('Offline compiler report failed.');
await writeFile(resolve(root, '.artifacts/vscode/compiled-evidence.json'), JSON.stringify(data, null, 2));
console.log('Prepared fresh offline compiler evidence and real/fake hosts; no database connection.');
