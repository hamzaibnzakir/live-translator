import { describe, expect, it } from 'vitest';
import { skipReason, shouldSkip } from '../../src/shared/skipRules';
import { normalizeText, splitWhitespace, fnv1a } from '../../src/shared/normalize';
import { canonicalLang, searchLanguages, toBcp47 } from '../../src/shared/languages';
import { parseHotkey, matchesHotkey, formatHotkey } from '../../src/shared/hotkey';
import { mergeSettings, DEFAULT_SETTINGS, pushRecent, channelMode } from '../../src/shared/settings';

describe('skip rules', () => {
  it.each([
    ['', 'empty'], ['   ', 'empty'], ['https://example.com/a?b=c', 'url'], ['www.discord.gg', 'url'], ['me@example.com', 'email'],
    ['12345', 'no-letters'], ['49,90 €', 'no-letters'], ['!!!', 'no-letters'], ['+1', 'no-letters'], ['😂😂', 'emoji'], ['👍🏽 🔥', 'emoji'],
    [':pepe_laugh: :kekw:', 'emoji'], ['lol', 'noise'], ['LOOOL', 'noise'], ['xD', 'noise'], ['hahaha', 'noise'], ['kkkkk', 'noise'], ['ㅋㅋㅋ', 'noise'],
    ['草草', 'noise'], ['gg', 'noise'], ['ok', 'noise'], ['a', 'too-short'],
  ])('%j → %s', (text, reason) => expect(skipReason(text)).toBe(reason));

  it.each(['Bonjour', 'こんにちは', '好', '안녕', 'Привет всем', '¿Qué tal?', 'Salut @David'])('keeps %j', (t) => expect(shouldSkip(t)).toBe(false));
});

describe('normalize', () => {
  it('collapses whitespace and NFC-normalizes', () => {
    expect(normalizeText('  Cáfe\n\n  au   lait ')).toBe('Cáfe au lait');
    expect(normalizeText('é')).toBe('é');
  });
  it('splits whitespace', () => expect(splitWhitespace('  hi there \n')).toEqual(['  ', 'hi there', ' \n']));
  it('hash is stable and differs', () => { expect(fnv1a('abc')).toBe(fnv1a('abc')); expect(fnv1a('abc')).not.toBe(fnv1a('abd')); });
});

describe('languages', () => {
  it('canonicalises BCP 47', () => {
    expect(canonicalLang('pt-BR')).toBe('pt');
    expect(canonicalLang('zh-Hant')).toBe('zh_hant');
    expect(canonicalLang('zh-TW')).toBe('zh_hant');
    expect(canonicalLang('zh-CN')).toBe('zh');
    expect(canonicalLang('fil')).toBe('tl');
    expect(canonicalLang('iw')).toBe('he');
    expect(toBcp47('zh_hant')).toBe('zh-Hant');
  });
  it('searches by name, native name and code', () => {
    expect(searchLanguages('japan')[0]!.code).toBe('ja');
    expect(searchLanguages('日本')[0]!.code).toBe('ja');
    expect(searchLanguages('fr').map((l) => l.code)).toContain('fr');
    expect(searchLanguages('portu')[0]!.code).toBe('pt');
  });
});

describe('hotkeys', () => {
  const ev = (o: Partial<KeyboardEvent>) => ({ ctrlKey: false, shiftKey: false, altKey: false, metaKey: false, key: '', code: '', ...o }) as KeyboardEvent;
  it('parses and matches Ctrl+Shift+Y independent of layout/shift case', () => {
    const hk = parseHotkey('Ctrl+Shift+Y');
    expect(hk).toEqual({ ctrl: true, shift: true, alt: false, meta: false, key: 'y' });
    expect(matchesHotkey(ev({ ctrlKey: true, shiftKey: true, key: 'Y', code: 'KeyY' }), hk)).toBe(true);
    expect(matchesHotkey(ev({ ctrlKey: true, shiftKey: true, key: 'z', code: 'KeyY' }), hk)).toBe(true); // AZERTY-independent via code
    expect(matchesHotkey(ev({ ctrlKey: true, key: 'y', code: 'KeyY' }), hk)).toBe(false);
  });
  it('formats a pressed combo', () => {
    expect(formatHotkey(ev({ ctrlKey: true, altKey: true, key: 'k', code: 'KeyK' }))).toBe('Ctrl+Alt+K');
    expect(formatHotkey(ev({ shiftKey: true, key: 'Shift', code: 'ShiftLeft' }))).toBeNull();
  });
});

describe('settings', () => {
  it('deep-merges partial stored settings over defaults', () => {
    const s = mergeSettings(DEFAULT_SETTINGS, { engine: 'bergamot', discord: { marker: false, servers: { '1': 'never' } } });
    expect(s.engine).toBe('bergamot');
    expect(s.discord.marker).toBe(false);
    expect(s.discord.replyHotkey).toBe('Ctrl+Shift+Y');
    expect(s.discord.servers).toEqual({ '1': 'never' });
    expect(s.polish.endpoint).toBe('http://localhost:1234/v1');
  });
  it('recent list is MRU, unique, capped', () => expect(pushRecent(['fr', 'es', 'ja'], 'es', 3)).toEqual(['es', 'fr', 'ja']));
  it('channel mode: channel > server > default', () => {
    const s = mergeSettings(DEFAULT_SETTINGS, { discord: { defaultMode: 'ask', servers: { g: 'never' }, channels: { 'g/c1': 'always' } } });
    expect(channelMode(s, 'g', 'c1')).toBe('always');
    expect(channelMode(s, 'g', 'c2')).toBe('never');
    expect(channelMode(s, 'x', 'c3')).toBe('ask');
  });
});
