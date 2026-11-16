// Small helpers used by the Blazor components (exposed as window.specops).
(() => {
  const themeKey = 'specops-theme';

  // localStorage can throw (privacy modes, disabled storage); treat that as "nothing stored".
  const storage = {
    get(key) {
      try {
        return localStorage.getItem(key);
      } catch {
        return null;
      }
    },
    set(key, value) {
      try {
        localStorage.setItem(key, value);
        return true;
      } catch {
        return false;
      }
    },
  };

  const specops = {
    storage,

    applyTheme() {
      const theme = storage.get(themeKey);
      const dark = theme === 'dark' || (!theme && matchMedia('(prefers-color-scheme: dark)').matches);
      document.documentElement.classList.toggle('dark', dark);
      window.dispatchEvent(new CustomEvent('specops-theme', { detail: { dark } }));
    },

    toggleTheme() {
      const dark = !document.documentElement.classList.contains('dark');
      storage.set(themeKey, dark ? 'dark' : 'light');
      specops.applyTheme();
    },

    isDark() {
      return document.documentElement.classList.contains('dark');
    },

    scrollToBottom(element) {
      if (element) {
        element.scrollTop = element.scrollHeight;
      }
    },

    // Clipboard API only (needs HTTPS or localhost). Returns whether the copy worked.
    async copyText(text) {
      if (!navigator.clipboard) {
        return false;
      }

      try {
        await navigator.clipboard.writeText(text);
        return true;
      } catch {
        return false;
      }
    },

    downloadText(fileName, text) {
      const url = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }));
      const link = Object.assign(document.createElement('a'), { href: url, download: fileName });
      document.body.appendChild(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    },

    // Monaco's editor.main.js finishes loading asynchronously; wait for it before creating editors.
    whenMonacoReady(timeoutMs = 20000) {
      return new Promise((resolve) => {
        const started = Date.now();
        const check = () => {
          if (window.monaco?.editor) {
            resolve(true);
          } else if (Date.now() - started > timeoutMs) {
            resolve(false);
          } else {
            setTimeout(check, 50);
          }
        };
        check();
      });
    },
  };

  window.specops = specops;

  // Keep the theme after Blazor enhanced navigation, which can reset <html> attributes.
  document.addEventListener('DOMContentLoaded', () => {
    window.Blazor?.addEventListener?.('enhancedload', specops.applyTheme);
  });
})();
