// Small helpers used by the Blazor components, exposed as window.specops (called via IJSRuntime by name).
// Loaded as a classic script before blazor.web.js; bundled to wwwroot/js/specops.js by build-client.mjs.

const themeKey = 'specops-theme';

// localStorage can throw (privacy modes, disabled storage); treat that as "nothing stored".
const storage = {
  get(key: string): string | null {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  },
  set(key: string, value: string): boolean {
    try {
      localStorage.setItem(key, value);
      return true;
    } catch {
      return false;
    }
  },
};

const isDark = (): boolean => document.documentElement.classList.contains('dark');

function applyTheme(): void {
  const theme = storage.get(themeKey);
  const dark = theme === 'dark' || (!theme && matchMedia('(prefers-color-scheme: dark)').matches);
  document.documentElement.classList.toggle('dark', dark);
  window.dispatchEvent(new CustomEvent<ThemeChangeDetail>('specops-theme', { detail: { dark } }));
}

function toggleTheme(): void {
  storage.set(themeKey, isDark() ? 'light' : 'dark');
  applyTheme();
}

function scrollToBottom(element: HTMLElement | null): void {
  if (element) {
    element.scrollTop = element.scrollHeight;
  }
}

/** Clipboard API only (needs HTTPS or localhost). Returns whether the copy worked. */
async function copyText(text: string): Promise<boolean> {
  // navigator.clipboard is undefined on insecure origins, despite what the DOM types say.
  if (!(navigator.clipboard as Clipboard | undefined)) {
    return false;
  }

  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}

function downloadText(fileName: string, text: string): void {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }));
  const link = Object.assign(document.createElement('a'), { href: url, download: fileName });
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => {
    URL.revokeObjectURL(url);
  }, 1000);
}

const specops = { storage, applyTheme, toggleTheme, isDark, scrollToBottom, copyText, downloadText };

declare global {
  interface Window {
    specops: typeof specops;
  }
}

window.specops = specops;

// Any element with data-theme-toggle switches the theme (works for static and interactive pages, CSP-safe).
document.addEventListener('click', (event) => {
  if (event.target instanceof Element && event.target.closest('[data-theme-toggle]')) {
    toggleTheme();
  }
});

// Keep the theme after Blazor enhanced navigation, which can reset <html> attributes.
document.addEventListener('DOMContentLoaded', () => {
  window.Blazor?.addEventListener?.('enhancedload', applyTheme);
});

export {};
