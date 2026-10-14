// A second opinion for language detection: distinctive grammar words ("les", "votre", "est",
// "pas"…) that statistical detectors underweight when a sentence is full of English jargon
// ("les gars votre extension trendtrack est down ou pas ?"). Lists hold words that are frequent
// in one language and rare in the others, plus common chat spellings.
import type { Detection } from './types';

const LISTS: Record<string, string> = {
  en: 'the is are was were you your i\'m im it\'s don\'t doesn\'t didn\'t can\'t won\'t isn\'t what who how why with this that these those have has had will would should could about from they them their there here and but because just really very my me we our i',
  fr: 'le la tout tous bonjour salut soir demain hier les des est sont ou pas je tu il elle nous vous votre vos notre nos mon ma mes ton ta tes son sa ses leur avec pour dans sur mais donc car qui quoi quand comment pourquoi aussi très tres bien merci oui non ça ca c\'est cest jsuis chui j\'ai jai t\'as tas y\'a ya gars mec frero frérot wesh stp svp bcp mdr ptdr trop déjà deja encore toujours rien faut fais fait peux veux sais moi toi lui eux une un du au aux ce cette ces',
  es: 'el los las es está esta están estan pero porque qué que cómo como dónde donde cuándo cuando yo tú tu usted nosotros ustedes muy gracias hola sí también tambien nada algo alguien quiere jugar noche hoy mañana manana tengo tienes hay estoy eres somos del una uno',
  pt: 'não nao você voce vocês voces obrigado obrigada também tambem muito está estao estão são sao isso isto aqui ali então entao pessoal tudo nada uma um do da dos das para pra com mas porque até ate amanhã amanha hoje',
  de: 'der die das und ist nicht ich du wir ihr sie ein eine einen mit auf für fur aber auch noch schon nur wer was wie warum heute morgen abend danke bitte ja nein zusammen möchte mochte spielen',
  it: 'il lo gli della delle è sono non ma perché perche che cosa come dove quando io tu noi voi grazie ciao anche molto oggi domani sera',
  tr: 've bir bu şu için icin ama çok cok değil degil ne nasıl nasil neden ben sen biz siz var yok merhaba teşekkürler tesekkurler arkadaşlar arkadaslar mı mi mu mü',
  nl: 'het een en niet ik jij je wij jullie zijn ook maar wat hoe waarom vandaag morgen bedankt dank hallo',
  pl: 'nie jest się sie że ze ale jak co dlaczego ja ty my wy dzięki dzieki cześć czesc bardzo też tez',
  id: 'yang dan tidak ini itu saya kamu kami kita ada apa bagaimana kenapa terima kasih juga sudah belum',
};
const SETS: Record<string, Set<string>> = Object.fromEntries(Object.entries(LISTS).map(([k, v]) => [k, new Set(v.split(/\s+/))]));
// Words shared by several languages are ignored for everyone ("a", "de", "que" in fr/es/pt…).
const SHARED = (() => {
  const count = new Map<string, number>();
  for (const s of Object.values(SETS)) for (const w of s) count.set(w, (count.get(w) ?? 0) + 1);
  return new Set([...count].filter(([, n]) => n > 1).map(([w]) => w));
})();

export function stopwordScores(text: string): Record<string, number> {
  const words = text.toLowerCase().normalize('NFC').replace(/[’']/g, '\'').match(/[\p{L}']+/gu) ?? [];
  const scores: Record<string, number> = {};
  for (const raw of words) {
    for (const w of new Set([raw, raw.replace(/'/g, '')])) {
      if (SHARED.has(w)) continue;
      for (const [lang, set] of Object.entries(SETS)) if (set.has(w)) scores[lang] = (scores[lang] ?? 0) + 1;
    }
  }
  return scores;
}

/**
 * Combines a statistical detection with grammar-word evidence and the channel's language.
 * Only changes the answer when the evidence is clear; otherwise returns `d` unchanged.
 */
export function refineDetection(text: string, d: Detection | null, o: { target: string; hint?: string }): Detection | null {
  const s = stopwordScores(text);
  const ranked = Object.entries(s).sort((a, b) => b[1] - a[1]);
  const [best, bestN] = ranked[0] ?? ['', 0];
  const second = ranked[1]?.[1] ?? 0;
  const en = s.en ?? 0;
  // 1) Clear grammar-word winner (2+ words, ahead of every other language, incl. English).
  if (best && bestN >= 2 && bestN > second && (!d || d.lang !== best)) {
    // Don't overrule a confident non-English detection that the words don't contradict.
    if (!d || d.lang === o.target || d.lang === 'en' || d.confidence < 0.75 || (s[d.lang] ?? 0) === 0) {
      return { lang: best, confidence: Math.min(0.95, 0.6 + 0.08 * (bestN - second)), alternatives: d ? [{ lang: d.lang, confidence: d.confidence }] : [] };
    }
  }
  // 2) Detector says "target" (or is unsure) but the text has words of the channel's language and none of English.
  if (o.hint && o.hint !== o.target && (s[o.hint] ?? 0) >= 1 && en === 0 && (!d || d.lang === o.target || d.confidence < 0.6 || (s[d.lang] ?? 0) === 0)) {
    return { lang: o.hint, confidence: 0.6, alternatives: d ? [{ lang: d.lang, confidence: d.confidence }] : [] };
  }
  // 3) A few words with no grammar words of any language ("Gen Justu Tactics", "GG EZ"): a guess
  //    like "Romanian" is noise. Treat it as the reader's own language, i.e. leave it alone.
  const wordCount = (text.match(/[\p{L}']+/gu) ?? []).length;
  if (d && ranked.length === 0 && wordCount <= 5 && d.lang !== o.hint && d.lang !== o.target && d.confidence < 0.7 && /^[\p{Script=Latin}\p{N}\p{P}\p{S}\s]+$/u.test(text)) {
    return { lang: o.target, confidence: 0.5, alternatives: [{ lang: d.lang, confidence: d.confidence }] };
  }
  return d;
}
