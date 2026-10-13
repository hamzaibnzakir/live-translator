export interface Hotkey { ctrl: boolean; shift: boolean; alt: boolean; meta: boolean; key: string }

/** Parses "Ctrl+Shift+Y" style strings. "Mod" means Ctrl (Cmd on macOS is matched as meta). */
export function parseHotkey(s: string): Hotkey | null {
  const parts = s.split('+').map((p) => p.trim()).filter(Boolean);
  if (!parts.length) return null;
  const hk: Hotkey = { ctrl: false, shift: false, alt: false, meta: false, key: '' };
  for (const p of parts) {
    const l = p.toLowerCase();
    if (l === 'ctrl' || l === 'control' || l === 'mod') hk.ctrl = true;
    else if (l === 'shift') hk.shift = true;
    else if (l === 'alt' || l === 'option') hk.alt = true;
    else if (l === 'meta' || l === 'cmd' || l === 'command') hk.meta = true;
    else hk.key = l.length === 1 ? l : l;
  }
  return hk.key ? hk : null;
}

export function matchesHotkey(e: Pick<KeyboardEvent, 'ctrlKey' | 'shiftKey' | 'altKey' | 'metaKey' | 'key' | 'code'>, hk: Hotkey | null): boolean {
  if (!hk) return false;
  if (e.ctrlKey !== hk.ctrl || e.shiftKey !== hk.shift || e.altKey !== hk.alt || e.metaKey !== hk.meta) return false;
  const key = e.key.toLowerCase();
  // `code` makes the shortcut independent of keyboard layout and of Shift changing `key`.
  const code = e.code.toLowerCase();
  return key === hk.key || code === `key${hk.key}` || code === `digit${hk.key}` || code === hk.key;
}

export function formatHotkey(e: Pick<KeyboardEvent, 'ctrlKey' | 'shiftKey' | 'altKey' | 'metaKey' | 'key' | 'code'>): string | null {
  if (['Control', 'Shift', 'Alt', 'Meta'].includes(e.key)) return null;
  const key = /^Key[A-Z]$/.test(e.code) ? e.code.slice(3) : /^Digit\d$/.test(e.code) ? e.code.slice(5) : e.key.length === 1 ? e.key.toUpperCase() : e.key;
  return [e.ctrlKey && 'Ctrl', e.altKey && 'Alt', e.shiftKey && 'Shift', e.metaKey && 'Meta', key].filter(Boolean).join('+');
}
