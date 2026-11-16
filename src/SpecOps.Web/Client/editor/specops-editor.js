// The code editor used by the Terminal page: Monaco (ES modules), PowerShell only.
// Loaded on demand from Blazor: import('./lib/editor/specops-editor.js').
import * as monaco from 'monaco-lean';

// Monaco runs its editor services in a web worker, served from this folder (CSP: worker-src 'self').
self.MonacoEnvironment = {
  getWorker: () => new Worker(new URL('./editor.worker.js', import.meta.url)),
};

// Bundled CSS (editor styles and the codicon font) lives next to this module.
const stylesheet = new URL('./specops-editor.css', import.meta.url).href;
if (!document.querySelector(`link[href="${stylesheet}"]`)) {
  document.head.appendChild(Object.assign(document.createElement('link'), { rel: 'stylesheet', href: stylesheet }));
}

const themeFor = (dark) => (dark ? 'vs-dark' : 'vs');

/**
 * Creates an editor in `element`. Ctrl/Cmd+Enter calls the .NET method `RunFromShortcut` on `dotnet`.
 * Returns a small handle Blazor holds as an IJSObjectReference.
 */
export function createEditor(element, value, dotnet) {
  const editor = monaco.editor.create(element, {
    value,
    language: 'powershell',
    theme: themeFor(document.documentElement.classList.contains('dark')),
    automaticLayout: true,
    fontSize: 13,
    minimap: { enabled: false },
    scrollBeyondLastLine: false,
  });

  editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, () => {
    dotnet.invokeMethodAsync('RunFromShortcut');
  });

  // Follow the app's light/dark toggle (specops.js raises this event).
  const onTheme = (event) => monaco.editor.setTheme(themeFor(event.detail.dark));
  window.addEventListener('specops-theme', onTheme);

  return {
    getValue: () => editor.getValue(),
    setValue: (text) => editor.setValue(text),
    focus: () => editor.focus(),
    dispose: () => {
      window.removeEventListener('specops-theme', onTheme);
      editor.dispose();
    },
  };
}
