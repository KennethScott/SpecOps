// Bundles the Monaco editor (ES modules) for the Terminal page into wwwroot/lib/editor/.
// Run by MSBuild before build (see SpecOps.Web.csproj) or manually: npm run build:editor
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import * as esbuild from 'esbuild';

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const monacoEditorDir = path.dirname(require.resolve('monaco-editor/editor/editor.main'));
const outdir = path.resolve(here, '../wwwroot/lib/editor');

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
    entryPoints: { 'specops-editor': path.join(here, 'editor/specops-editor.js') },
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
]);
