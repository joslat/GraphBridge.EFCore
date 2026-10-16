import { requireMemory } from './runtime.mjs';
requireMemory();
import { build } from '../../extensions/graphbridge-query-lab/node_modules/esbuild/lib/main.js';
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
const root = resolve(import.meta.dirname, '../..');
const suite = process.argv[2] ?? 'evidence';
if (!['evidence', 'host'].includes(suite)) throw new Error('Unknown suite');
const outfile = resolve(root, `.artifacts/vscode/${suite}.test.cjs`);
await build({ entryPoints: [resolve(import.meta.dirname, `${suite}.test.ts`)], outfile, bundle: true,
  platform: 'node', format: 'cjs', jsx: 'automatic', tsconfig: resolve(root, 'extensions/graphbridge-query-lab/tsconfig.json') });
const result = spawnSync(process.execPath, ['--test', outfile], { env: { ...process.env, GRAPHBRIDGE_ROOT: root }, stdio: 'inherit' });
process.exitCode = result.status ?? 1;
