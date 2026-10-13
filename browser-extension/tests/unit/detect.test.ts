import { describe, expect, it } from 'vitest';
import { chineseVariant, confidenceFrom, detectOffline } from '../../src/engines/detect';

describe('offline language detection', () => {
  it.each([
    ['Bonjour tout le monde, comment ça va ce soir ?', 'fr'],
    ['¿Alguien quiere jugar esta noche con nosotros?', 'es'],
    ['Obrigado pela ajuda, pessoal, até amanhã!', 'pt'],
    ['Merhaba arkadaşlar, bu akşam oynayalım mı?', 'tr'],
    ['Привет всем, кто хочет поиграть сегодня вечером?', 'ru'],
    ['مرحبا بالجميع، هل تريدون اللعب الليلة؟', 'ar'],
    ['こんにちは、元気ですか？', 'ja'],
    ['今日はとても暑いですね', 'ja'],
    ['안녕하세요, 오늘 밤에 게임 할래요?', 'ko'],
    ['大家好，今天晚上一起玩游戏吧', 'zh'],
    ['大家好，今天晚上一起玩遊戲吧，這個很好', 'zh_hant'],
    ['Hallo zusammen, wer möchte heute Abend spielen?', 'de'],
    ['I think this is a great idea, see you tonight', 'en'],
  ])('%s → %s', (text, lang) => {
    const d = detectOffline(text);
    expect(d?.lang).toBe(lang);
    expect(d!.confidence).toBeGreaterThanOrEqual(0.5);
  });

  it('short ambiguous text gets low confidence', () => {
    const d = detectOffline('salut');
    expect(d === null || d.confidence < 0.5).toBe(true);
  });
  it('chinese variant', () => { expect(chineseVariant('这个说')).toBe('zh'); expect(chineseVariant('這個說')).toBe('zh_hant'); });
  it('confidence model', () => {
    expect(confidenceFrom({ fr: 0.83, it: 0.68 }, true)).toBeCloseTo(0.85, 2);
    expect(confidenceFrom({ ca: 0.8, es: 0.64 }, false)).toBeLessThan(0.5);
  });
});
