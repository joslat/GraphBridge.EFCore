import { build } from 'esbuild';
import { readFile, writeFile } from 'node:fs/promises';
await build({ entryPoints: ['src/extension.ts'], outfile: 'dist/extension.cjs', bundle: true,
  platform: 'node', format: 'cjs', target: 'node22', external: ['vscode'], legalComments: 'linked' });
await build({ entryPoints: ['src/ui/main.tsx'], outfile: 'dist/webview.js', bundle: true,
  platform: 'browser', target: 'chrome130', minify: true, legalComments: 'linked',
  define: { 'process.env.NODE_ENV': '"production"' } });
const notices = ['# Bundled third-party licenses\n\nGraphBridge Query Lab is MIT licensed. The bundled runtime includes the following packages. Build-only tools are not shipped.\n'];
for (const name of ['react', 'react-dom', 'scheduler', 'zod']) {
  const pkg = JSON.parse(await readFile(`node_modules/${name}/package.json`, 'utf8'));
  notices.push(`## ${name} ${pkg.version}\n\n${await readFile(`node_modules/${name}/LICENSE`, 'utf8')}\n`);
}
await writeFile('THIRD_PARTY_NOTICES.md', notices.join('\n'));
