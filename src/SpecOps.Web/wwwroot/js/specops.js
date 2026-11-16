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
  };

  window.specops = specops;

  // Any element with data-theme-toggle switches the theme (works for static and interactive pages, CSP-safe).
  document.addEventListener('click', (event) => {
    if (event.target instanceof Element && event.target.closest('[data-theme-toggle]')) {
      specops.toggleTheme();
    }
  });

  // Keep the theme after Blazor enhanced navigation, which can reset <html> attributes.
  document.addEventListener('DOMContentLoaded', () => {
    window.Blazor?.addEventListener?.('enhancedload', specops.applyTheme);
  });
})();
