let on: boolean | null = null;
/** console.debug, only when localStorage.bbDebug === '1' on the page (for troubleshooting selectors). */
export function debug(...args: unknown[]): void {
  if (on === null) { try { on = localStorage.getItem('bbDebug') === '1'; } catch { on = false; } }
  if (on) console.debug('[Brainbox]', ...args);
}
