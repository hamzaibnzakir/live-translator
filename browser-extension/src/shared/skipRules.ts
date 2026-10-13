// Cheap checks that run before any detection: text that should never be sent to an engine.

const URL_RE = /^(?:https?:\/\/|www\.)\S+$/i;
const URL_ANY_RE = /(?:https?:\/\/|www\.)\S+/gi;
const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
// Digits, punctuation, symbols, whitespace only (prices, scores, dates, "!!!", "...", "+1").
const NO_LETTERS_RE = /^[\p{N}\p{P}\p{S}\s]*$/u;
// Emoji / pictographs / modifiers / ZWJ / variation selectors / Discord :shortcodes: only.
const EMOJI_ONLY_RE = /^(?:[\p{Extended_Pictographic}\p{Emoji_Modifier}\p{Emoji_Component}‍️\s]|:[\w~-]+:)+$/u;
const LETTER_RE = /\p{L}/gu;
// Laughs, keyboard mashes and other short noise that every language shares.
const NOISE_RE = /^(?:(?:ha|he|hi|ja|je|ka|ku|xa|kk|rs|w)+|l+o+l+|x+d+|o+k+|k+|g+g+|h+m+|u+h+|a+h+|o+h+|5{3,}|w{2,}|草+|ㅋ+|ㅎ+|ахах[ах]*|хаха[ха]*)$/iu;

export type SkipReason = 'empty' | 'url' | 'email' | 'no-letters' | 'emoji' | 'noise' | 'too-short';

export function skipReason(text: string): SkipReason | null {
  const t = text.trim();
  if (!t) return 'empty';
  if (URL_RE.test(t)) return 'url';
  if (EMAIL_RE.test(t)) return 'email';
  if (EMOJI_ONLY_RE.test(t) && /[^\d\s#*]/u.test(t)) return 'emoji';
  if (NO_LETTERS_RE.test(t)) return 'no-letters';
  const compact = t.replace(/[\s\p{P}\p{S}]+/gu, '');
  if (NOISE_RE.test(compact)) return 'noise';
  // Letters remaining after URLs are removed. CJK carries meaning per character, so 1 is enough there.
  const letters = (t.replace(URL_ANY_RE, '').match(LETTER_RE) ?? []).length;
  const cjk = /[\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}\p{Script=Hangul}]/u.test(t);
  if (letters === 0) return 'no-letters';
  if (!cjk && letters < 2) return 'too-short';
  return null;
}

export function shouldSkip(text: string): boolean {
  return skipReason(text) !== null;
}
