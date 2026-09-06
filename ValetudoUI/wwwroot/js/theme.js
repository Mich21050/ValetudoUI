// Theme switch: 'system' (follow the OS), 'light' or 'dark', remembered per browser.
// Loaded in <head> before the stylesheets so the attribute is in place before first paint.
window.valetudoTheme = {
  get() { try { return localStorage.getItem('theme') || 'system'; } catch (e) { return 'system'; } },
  set(mode) {
    try { if (mode === 'system') localStorage.removeItem('theme'); else localStorage.setItem('theme', mode); } catch (e) { /* private mode */ }
    this.apply();
    return this.get();
  },
  apply() {
    const mode = this.get();
    const root = document.documentElement;
    if (mode === 'system') root.removeAttribute('data-theme'); else root.setAttribute('data-theme', mode);
    const dark = mode === 'dark' || (mode === 'system' && window.matchMedia && matchMedia('(prefers-color-scheme: dark)').matches);
    const meta = document.querySelector('meta[name=theme-color]');
    if (meta) meta.setAttribute('content', dark ? '#1c1c1e' : '#ffffff');
    document.dispatchEvent(new CustomEvent('valetudo-theme', { detail: { mode, dark } }));
  }
};
window.valetudoTheme.apply();
if (window.matchMedia) matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => window.valetudoTheme.apply());
