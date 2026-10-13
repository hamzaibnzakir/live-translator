// Splits a Discord-markdown draft into translatable text and protected tokens (code, mentions,
// emoji, links, timestamps, markdown markers) so a translation can never break them.

export interface Piece { text: string; protected: boolean }

const TOKEN_SOURCES = [
  '```[\\s\\S]*?```',                     // code block
  '`[^`\\n]+`',                           // inline code
  '<(?:@[!&]?|#)\\d+>',                   // raw user / role / channel mention
  '<a?:\\w+:\\d+>',                       // custom emoji
  '<t:\\d+(?::[tTdDfFR])?>',              // timestamp
  '</[\\w -]+:\\d+>',                     // slash command mention
  '<?(?:https?://|www\\.)[^\\s>]+>?',     // links (also <https://…> embeds suppressed)
  '\\[[^\\]\\n]*\\]\\([^)\\s]+\\)',        // masked link [text](url)
  '@(?:everyone|here)\\b',
  '(?<![\\w@])@[\\w.]{2,32}',             // @username
  '(?<![\\w&])#[\\w-]{2,100}',            // #channel
  ':[\\w~+-]{2,32}:',                     // :emoji:
  '\\|\\|',                               // spoiler markers
  '~~',
  '\\*{1,3}',
  '(?<!\\w)_{1,3}|_{1,3}(?!\\w)',          // underscores as markup, not inside snake_case
  '^>>> |^> |^#{1,3} |^-# |^\\s*[-*] |^\\s*\\d+\\. ', // quote / heading / subtext / list prefixes
  '(?:\\p{Extended_Pictographic}|\\u200d|\\ufe0f)+',  // unicode emoji
];
const TOKEN_RE = new RegExp(TOKEN_SOURCES.join('|'), 'gmu');
const HAS_LETTER = /\p{L}/u;

export function splitProtected(text: string): Piece[] {
  const pieces: Piece[] = [];
  let last = 0;
  for (const m of text.matchAll(TOKEN_RE)) {
    const i = m.index ?? 0;
    if (!m[0]) continue;
    if (i > last) pieces.push({ text: text.slice(last, i), protected: false });
    pieces.push({ text: m[0], protected: true });
    last = i + m[0].length;
  }
  if (last < text.length) pieces.push({ text: text.slice(last), protected: false });
  // Pieces without letters (spaces, punctuation) are kept verbatim too.
  return pieces.map((p) => (p.protected || HAS_LETTER.test(p.text) ? p : { ...p, protected: true }));
}

/** The parts of a draft that need translating, in order. */
export function translatableParts(pieces: Piece[]): string[] {
  return pieces.filter((p) => !p.protected).map((p) => p.text.trim());
}

/** Re-assembles a draft, putting each translated part back where its source was (keeping its whitespace). */
export function joinProtected(pieces: Piece[], translated: string[]): string {
  let k = 0;
  return pieces.map((p) => {
    if (p.protected) return p.text;
    const lead = /^\s*/.exec(p.text)?.[0] ?? '';
    const trail = /\s*$/.exec(p.text)?.[0] ?? '';
    const t = translated[k++] ?? p.text.trim();
    return lead + t + trail;
  }).join('');
}

/** Translates a draft line by line, protecting tokens; `translate` receives the plain parts in one batch. */
export async function translateProtected(text: string, translate: (parts: string[]) => Promise<string[]>): Promise<string> {
  const lines = text.split('\n');
  const perLine = lines.map(splitProtected);
  const parts = perLine.flatMap(translatableParts);
  if (!parts.length) return text;
  const out = await translate(parts);
  let k = 0;
  return perLine.map((pieces) => {
    const n = translatableParts(pieces).length;
    const slice = out.slice(k, k + n);
    k += n;
    return joinProtected(pieces, slice);
  }).join('\n');
}
