// Offline language detection: Unicode-script analysis for unambiguous scripts, ELD n-grams
// (Apache-2.0, bundled, no network) for the rest. Chrome's LanguageDetector is preferred when
// it is ready; this is the always-available fallback.
import { eld } from 'eld/medium';
import { LANGUAGES } from '../shared/languages';
import type { Detection } from './types';

const SCRIPT_LANG: [RegExp, string][] = [
  [/[\p{Script=Hiragana}\p{Script=Katakana}]/u, 'ja'],
  [/\p{Script=Hangul}/u, 'ko'],
  [/\p{Script=Thai}/u, 'th'],
  [/\p{Script=Hebrew}/u, 'he'],
  [/\p{Script=Greek}/u, 'el'],
  [/\p{Script=Bengali}/u, 'bn'],
  [/\p{Script=Tamil}/u, 'ta'],
  [/\p{Script=Telugu}/u, 'te'],
  [/\p{Script=Gujarati}/u, 'gu'],
  [/\p{Script=Kannada}/u, 'kn'],
  [/\p{Script=Malayalam}/u, 'ml'],
];

const TRAD_ONLY = '們個這說時會來對學國實還關經進長開問頭點東車門見現發電話麼樣邊後書買賣讓認識體愛應從將與當為無聽過'.split('');
const SIMP_ONLY = '们个这说时会来对学国实还关经进长开问头点东车门见现发电话么样边后书买卖让认识体爱应从将与当为无听过'.split('');

export function chineseVariant(text: string): 'zh' | 'zh_hant' {
  let t = 0, s = 0;
  for (const ch of text) { if (TRAD_ONLY.includes(ch)) t++; else if (SIMP_ONLY.includes(ch)) s++; }
  return t > s ? 'zh_hant' : 'zh';
}

function scriptShare(text: string, re: RegExp): number {
  let hit = 0, letters = 0;
  for (const ch of text) {
    if (/\p{L}/u.test(ch)) { letters++; if (re.test(ch)) hit++; }
  }
  return letters ? hit / letters : 0;
}

const SUPPORTED = new Set(LANGUAGES.map((l) => l.code));
const detector = eld.newInstance();
detector.setLanguageSubset([...SUPPORTED].filter((c) => c.length === 2));

/** Maps ELD's score spread to a 0..1 confidence; unreliable results stay below 0.5. */
export function confidenceFrom(scores: Record<string, number>, reliable: boolean): number {
  const vals = Object.values(scores).sort((a, b) => b - a);
  const top = vals[0] ?? 0, second = vals[1] ?? 0;
  const gap = top - second;
  return reliable ? Math.min(1, 0.55 + gap * 2) : Math.min(0.45, 0.2 + gap);
}

export function detectOffline(text: string): Detection | null {
  const t = text.trim();
  if (!t) return null;
  for (const [re, lang] of SCRIPT_LANG) {
    const share = scriptShare(t, re);
    if (share >= 0.3 || (lang === 'ja' && share > 0)) return { lang, confidence: Math.min(1, 0.7 + share * 0.3) };
  }
  // Han without kana/hangul: Chinese.
  const han = scriptShare(t, /\p{Script=Han}/u);
  if (han >= 0.5) return { lang: chineseVariant(t), confidence: Math.min(1, 0.6 + han * 0.4) };

  const r = detector.detect(t);
  if (!r.language) return null;
  const scores = r.getScores();
  const entries = Object.entries(scores).sort((a, b) => b[1] - a[1]);
  return {
    lang: r.language,
    confidence: confidenceFrom(scores, r.isReliable()),
    alternatives: entries.slice(1, 4).map(([lang, s]) => ({ lang, confidence: s })),
  };
}
