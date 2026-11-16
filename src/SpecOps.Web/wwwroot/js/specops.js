// Small helpers used by the Blazor components.
window.specops = {
  applyTheme() {
    let theme = null;
    try { theme = localStorage.getItem('specops-theme'); } catch { }
    const dark = theme === 'dark' || (!theme && matchMedia('(prefers-color-scheme: dark)').matches);
    document.documentElement.classList.toggle('dark', dark);
    window.dispatchEvent(new CustomEvent('specops-theme', { detail: { dark } }));
  },

  toggleTheme() {
    const dark = !document.documentElement.classList.contains('dark');
    try { localStorage.setItem('specops-theme', dark ? 'dark' : 'light'); } catch { }
    specops.applyTheme();
  },

  isDark() {
    return document.documentElement.classList.contains('dark');
  },

  scrollToBottom(element) {
    if (element) element.scrollTop = element.scrollHeight;
  },

  async copyText(text) {
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      const area = Object.assign(document.createElement('textarea'), { value: text });
      document.body.appendChild(area);
      area.select();
      document.execCommand('copy');
      area.remove();
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
    return new Promise(resolve => {
      const started = Date.now();
      (function check() {
        if (window.monaco?.editor) return resolve(true);
        if (Date.now() - started > timeoutMs) return resolve(false);
        setTimeout(check, 50);
      })();
    });
  },

  storage: {
    get(key) { try { return localStorage.getItem(key); } catch { return null; } },
    set(key, value) { try { localStorage.setItem(key, value); } catch { } },
  },
};

// Keep the theme after Blazor enhanced navigation, which can reset <html> attributes.
document.addEventListener('DOMContentLoaded', () => {
  if (window.Blazor?.addEventListener) {
    Blazor.addEventListener('enhancedload', specops.applyTheme);
  }
});
