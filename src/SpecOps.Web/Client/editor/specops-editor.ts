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

const themeFor = (dark: boolean): string => (dark ? 'vs-dark' : 'vs');

/** The handle Blazor holds (as an IJSObjectReference) for an editor created by {@link createEditor}. */
export interface EditorHandle {
  getValue(): string;
  setValue(text: string): void;
  focus(): void;
  dispose(): void;
}

/** .NET methods (on the `dotnet` object) that keyboard shortcuts call. */
export interface EditorShortcuts {
  /** Ctrl/Cmd+Enter. */
  run?: string;
  /** Ctrl/Cmd+S (instead of the browser's "save page"). */
  save?: string;
}

/** Creates an editor in `element`; by default Ctrl/Cmd+Enter calls the .NET method `RunFromShortcut`. */
export function createEditor(
  element: HTMLElement,
  value: string,
  dotnet: DotNetObject,
  shortcuts: EditorShortcuts = { run: 'RunFromShortcut' },
): EditorHandle {
  const editor = monaco.editor.create(element, {
    value,
    language: 'powershell',
    theme: themeFor(document.documentElement.classList.contains('dark')),
    automaticLayout: true,
    fontSize: 13,
    minimap: { enabled: false },
    scrollBeyondLastLine: false,
  });

  const bind = (key: number, method: string | undefined): void => {
    if (method) {
      editor.addCommand(monaco.KeyMod.CtrlCmd | key, () => {
        void dotnet.invokeMethodAsync(method);
      });
    }
  };
  bind(monaco.KeyCode.Enter, shortcuts.run);
  bind(monaco.KeyCode.KeyS, shortcuts.save);

  // Follow the app's light/dark toggle (specops.ts raises this event).
  const onTheme = (event: CustomEvent<ThemeChangeDetail>): void => {
    monaco.editor.setTheme(themeFor(event.detail.dark));
  };
  window.addEventListener('specops-theme', onTheme);

  return {
    getValue: () => editor.getValue(),
    setValue: (text) => {
      editor.setValue(text);
    },
    focus: () => {
      editor.focus();
    },
    dispose: () => {
      window.removeEventListener('specops-theme', onTheme);
      editor.dispose();
    },
  };
}
