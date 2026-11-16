// Types for things the browser code uses but doesn't import.

// The trimmed Monaco build assembled by build-client.mjs (see the monaco-lean plugin): same API as monaco-editor.
declare module 'monaco-lean' {
  export * from 'monaco-editor';
}

/** The parts of Blazor's client API (blazor.web.js) that SpecOps uses. */
interface BlazorClient {
  reconnect(): Promise<boolean>;
  resumeCircuit(): Promise<boolean>;
  addEventListener?(name: 'enhancedload', callback: () => void): void;
}

/** A .NET object reference passed to JavaScript (DotNetObjectReference). */
interface DotNetObject {
  invokeMethodAsync<T = void>(methodName: string, ...args: unknown[]): Promise<T>;
}

/** Detail of the `specops-theme` event raised whenever the light/dark theme is applied. */
interface ThemeChangeDetail {
  dark: boolean;
}

/** Detail of Blazor's `components-reconnect-state-changed` event. */
interface ReconnectStateDetail {
  state: 'show' | 'hide' | 'retrying' | 'failed' | 'rejected' | 'resume-failed';
}

interface WindowEventMap {
  'specops-theme': CustomEvent<ThemeChangeDetail>;
}

// eslint-disable-next-line no-var -- global declarations must use var
declare var Blazor: BlazorClient | undefined;
