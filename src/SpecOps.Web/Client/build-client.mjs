// Compiles the browser code in Client/ (TypeScript) into wwwroot:
//   specops.ts         -> wwwroot/js/specops.js          (classic script, every page)
//   reconnect-modal.ts -> wwwroot/js/reconnect-modal.js  (ES module, Blazor's reconnect dialog)
//   editor/            -> wwwroot/lib/editor/            (Monaco editor bundle, Terminal page only)
// esbuild only strips types; `npm run typecheck` checks them. Run by MSBuild before build (see SpecOps.Web.csproj)
// or manually: npm run build:client
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import * as esbuild from 'esbuild';

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const monacoEditorDir = path.dirname(require.resolve('monaco-editor/editor/editor.main'));
const wwwroot = path.resolve(here, '../wwwroot');
const outdir = path.join(wwwroot, 'lib/editor');

/**
 * "monaco-lean": Monaco's own editor.main.js minus the ~80 language definitions and the TypeScript/CSS/HTML/JSON
 * language services, plus PowerShell. Derived from editor.main.js so editor features stay in sync with Monaco updates.
 */
const monacoLean = {
  name: 'monaco-lean',
  setup(build) {
    build.onResolve({ filter: /^monaco-lean$/ }, () => ({ path: 'monaco-lean', namespace: 'monaco-lean' }));
    build.onLoad({ filter: /.*/, namespace: 'monaco-lean' }, async () => {
      const main = await readFile(path.join(monacoEditorDir, 'editor.main.js'), 'utf8');
      const kept = main
        .split('\n')
        .filter((line) => /^(import|export) /.test(line))
        // Drop languages, language services and the LSP client (and the exports that re-export them).
        .filter(
          (line) =>
            !/languages\/(definitions|features)\/|monaco-lsp-client|^export \{ (__src_|index as lsp)/.test(line),
        );
      kept.push("import '../languages/definitions/powershell/register.js';");
      return { contents: kept.join('\n'), resolveDir: monacoEditorDir, loader: 'js' };
    });
  },
};

const common = { bundle: true, minify: true, legalComments: 'none', logLevel: 'warning', target: 'es2022' };

await Promise.all([
  esbuild.build({
    ...common,
    entryPoints: { 'specops-editor': path.join(here, 'editor/specops-editor.ts') },
    outdir,
    format: 'esm',
    splitting: true,
    chunkNames: 'chunks/[name]-[hash]',
    assetNames: 'assets/[name]-[hash]',
    loader: { '.ttf': 'file' },
    plugins: [monacoLean],
  }),
  esbuild.build({
    ...common,
    entryPoints: { 'editor.worker': path.join(monacoEditorDir, 'editor.worker.js') },
    outdir,
    format: 'iife',
  }),
  esbuild.build({
    ...common,
    entryPoints: { specops: path.join(here, 'specops.ts') },
    outdir: path.join(wwwroot, 'js'),
    format: 'iife',
  }),
  esbuild.build({
    ...common,
    entryPoints: { 'reconnect-modal': path.join(here, 'reconnect-modal.ts') },
    outdir: path.join(wwwroot, 'js'),
    format: 'esm',
  }),
]);
