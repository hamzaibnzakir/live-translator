/** Normalises text for cache keys: NFC, collapsed whitespace, trimmed. Case is kept (it matters for MT). */
export function normalizeText(text: string): string {
  return text.normalize('NFC').replace(/\s+/g, ' ').trim();
}

/** Splits text into [leading whitespace, core, trailing whitespace]. */
export function splitWhitespace(text: string): [string, string, string] {
  const m = /^(\s*)([\s\S]*?)(\s*)$/.exec(text);
  return m ? [m[1] ?? '', m[2] ?? '', m[3] ?? ''] : ['', text, ''];
}

/** Small, fast, non-cryptographic hash (FNV-1a 32-bit) for change detection. */
export function fnv1a(text: string): string {
  let h = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return (h >>> 0).toString(36);
}
