/** Language catalogue. `code` is our canonical code (Bergamot/Firefox style, e.g. `zh_hant`). */
export interface Language {
  code: string;
  name: string;
  native: string;
  flag: string;
  /** BCP 47 tag used by Chrome's built-in Translator / LanguageDetector APIs. */
  bcp47: string;
}

const L = (code: string, name: string, native: string, flag: string, bcp47 = code): Language => ({ code, name, native, flag, bcp47 });

export const LANGUAGES: readonly Language[] = [
  L('en', 'English', 'English', '🇬🇧'),
  L('af', 'Afrikaans', 'Afrikaans', '🇿🇦'),
  L('ar', 'Arabic', 'العربية', '🇸🇦'),
  L('az', 'Azerbaijani', 'Azərbaycanca', '🇦🇿'),
  L('be', 'Belarusian', 'Беларуская', '🇧🇾'),
  L('bg', 'Bulgarian', 'Български', '🇧🇬'),
  L('bn', 'Bengali', 'বাংলা', '🇧🇩'),
  L('bs', 'Bosnian', 'Bosanski', '🇧🇦'),
  L('ca', 'Catalan', 'Català', '🇦🇩'),
  L('cs', 'Czech', 'Čeština', '🇨🇿'),
  L('da', 'Danish', 'Dansk', '🇩🇰'),
  L('de', 'German', 'Deutsch', '🇩🇪'),
  L('el', 'Greek', 'Ελληνικά', '🇬🇷'),
  L('es', 'Spanish', 'Español', '🇪🇸'),
  L('et', 'Estonian', 'Eesti', '🇪🇪'),
  L('eu', 'Basque', 'Euskara', '🏳️'),
  L('fa', 'Persian', 'فارسی', '🇮🇷'),
  L('fi', 'Finnish', 'Suomi', '🇫🇮'),
  L('fr', 'French', 'Français', '🇫🇷'),
  L('gl', 'Galician', 'Galego', '🏳️'),
  L('gu', 'Gujarati', 'ગુજરાતી', '🇮🇳'),
  L('hbs', 'Serbo-Croatian', 'Srpskohrvatski', '🏳️', 'sh'),
  L('he', 'Hebrew', 'עברית', '🇮🇱'),
  L('hi', 'Hindi', 'हिन्दी', '🇮🇳'),
  L('hr', 'Croatian', 'Hrvatski', '🇭🇷'),
  L('hu', 'Hungarian', 'Magyar', '🇭🇺'),
  L('id', 'Indonesian', 'Bahasa Indonesia', '🇮🇩'),
  L('is', 'Icelandic', 'Íslenska', '🇮🇸'),
  L('it', 'Italian', 'Italiano', '🇮🇹'),
  L('ja', 'Japanese', '日本語', '🇯🇵'),
  L('kn', 'Kannada', 'ಕನ್ನಡ', '🇮🇳'),
  L('ko', 'Korean', '한국어', '🇰🇷'),
  L('lt', 'Lithuanian', 'Lietuvių', '🇱🇹'),
  L('lv', 'Latvian', 'Latviešu', '🇱🇻'),
  L('ml', 'Malayalam', 'മലയാളം', '🇮🇳'),
  L('mr', 'Marathi', 'मराठी', '🇮🇳'),
  L('ms', 'Malay', 'Bahasa Melayu', '🇲🇾'),
  L('nb', 'Norwegian Bokmål', 'Norsk bokmål', '🇳🇴'),
  L('nl', 'Dutch', 'Nederlands', '🇳🇱'),
  L('no', 'Norwegian', 'Norsk', '🇳🇴'),
  L('pl', 'Polish', 'Polski', '🇵🇱'),
  L('pt', 'Portuguese', 'Português', '🇧🇷'),
  L('ro', 'Romanian', 'Română', '🇷🇴'),
  L('ru', 'Russian', 'Русский', '🇷🇺'),
  L('sk', 'Slovak', 'Slovenčina', '🇸🇰'),
  L('sl', 'Slovenian', 'Slovenščina', '🇸🇮'),
  L('sq', 'Albanian', 'Shqip', '🇦🇱'),
  L('sr', 'Serbian', 'Српски', '🇷🇸'),
  L('sv', 'Swedish', 'Svenska', '🇸🇪'),
  L('ta', 'Tamil', 'தமிழ்', '🇮🇳'),
  L('te', 'Telugu', 'తెలుగు', '🇮🇳'),
  L('th', 'Thai', 'ไทย', '🇹🇭'),
  L('tl', 'Tagalog', 'Tagalog', '🇵🇭', 'fil'),
  L('tr', 'Turkish', 'Türkçe', '🇹🇷'),
  L('ug', 'Uyghur', 'ئۇيغۇرچە', '🏳️'),
  L('uk', 'Ukrainian', 'Українська', '🇺🇦'),
  L('ur', 'Urdu', 'اردو', '🇵🇰'),
  L('vi', 'Vietnamese', 'Tiếng Việt', '🇻🇳'),
  L('zh', 'Chinese (Simplified)', '简体中文', '🇨🇳', 'zh'),
  L('zh_hant', 'Chinese (Traditional)', '繁體中文', '🇹🇼', 'zh-Hant'),
];

const BY_CODE = new Map(LANGUAGES.map((l) => [l.code, l]));

/** Normalises any incoming code (BCP 47 like `pt-BR`, `zh-Hant`, `zh-TW`, `fil`) to our canonical code. */
export function canonicalLang(code: string | null | undefined): string {
  if (!code) return 'und';
  const c = code.trim().replace('_', '-').toLowerCase();
  if (c === 'zh-hant' || c === 'zh-tw' || c === 'zh-hk' || c === 'zh-mo') return 'zh_hant';
  if (c.startsWith('zh')) return 'zh';
  if (c === 'fil') return 'tl';
  if (c === 'iw') return 'he';
  if (c === 'in') return 'id';
  if (c === 'sh') return 'hbs';
  const base = c.split('-')[0] ?? c;
  return BY_CODE.has(base) ? base : base;
}

export function getLanguage(code: string): Language | undefined {
  return BY_CODE.get(canonicalLang(code));
}

export function languageName(code: string): string {
  return getLanguage(code)?.name ?? code;
}

export function languageLabel(code: string): string {
  const l = getLanguage(code);
  return l ? `${l.flag} ${l.name}` : code;
}

export function toBcp47(code: string): string {
  return getLanguage(code)?.bcp47 ?? code;
}

/** Search by English name, native name or code (used by language dropdowns). */
export function searchLanguages(query: string): Language[] {
  const q = query.trim().toLowerCase();
  if (!q) return [...LANGUAGES];
  return LANGUAGES.filter((l) => l.code === q || l.name.toLowerCase().includes(q) || l.native.toLowerCase().includes(q));
}
