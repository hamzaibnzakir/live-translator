import { describe, expect, it } from 'vitest';
import { refineDetection, stopwordScores } from '../../src/engines/stopwords';
import { detectOffline } from '../../src/engines/detect';

// Real messages from Discord screenshots (French ecom servers, mixed with English jargon).
const FRENCH = [
  'les gars votre extension trendtrack est down ou pas ?',
  "Si des personnes ont des questions sur l'organique n'hésitez pas jsuis pratiquement à 100% d'orga",
  'Tu fais comment tes vidéos organiques ?',
  'tu fais de l’orga a coter des ads ?',
  'Tu répond DM mon reuf v',
  'Live ce soir ?',
  'Regarde dm stp',
  'je sais pas',
  'ok merci',
  'le ROAS est bon mais le CPM est trop cher sur meta',
  'Oui, je l’ai pris, tu as des questions ?',
];
const ENGLISH = [
  'I try trackbee!',
  "Guys please I'm on my first day of testing I made 4 ROAS sales of 2.4. Margin by 35% Am I increasing the campaign budget?",
  '1 ATC out of 5 tracks per meta can I worry?',
  'I have the same shit right now yet wetracked has them well',
  'he went from bro to frero',
  '20% we agree',
  'Gen Justu Tactics',
  "I don't know if the problem comes from wetracked because today I have 100% of my sales on wetrack but 60% on meta",
];

const detect = (t: string, hint?: string) => refineDetection(t, detectOffline(t), { target: 'en', hint });

describe('stopword second opinion', () => {
  it.each(FRENCH)('French: %s', (t) => {
    const d = detect(t, 'fr');
    expect(d?.lang).toBe('fr');
    expect(d!.confidence).toBeGreaterThanOrEqual(0.5);
  });
  it.each(ENGLISH)('English stays English: %s', (t) => {
    expect(detect(t, 'fr')?.lang ?? 'en').toBe('en');
  });
  it('the decisive mixed case works even without a channel hint', () => {
    expect(detect('les gars votre extension trendtrack est down ou pas ?')?.lang).toBe('fr');
  });
  it('other languages are not turned into French', () => {
    expect(detect('¿Alguien quiere jugar esta noche?', 'fr')?.lang).toBe('es');
    expect(detect('Obrigado pela ajuda, pessoal, até amanhã!', 'fr')?.lang).toBe('pt');
    expect(detect('Hallo zusammen, wer möchte heute Abend spielen?', 'fr')?.lang).toBe('de');
  });
  it('scores count distinctive words only', () => {
    const s = stopwordScores('les gars votre extension est down ou pas');
    expect(s.fr).toBeGreaterThanOrEqual(5);
    expect(s.en ?? 0).toBe(0);
  });
});
