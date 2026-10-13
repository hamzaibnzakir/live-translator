// Chrome's built-in, on-device Translator + Language Detector APIs (desktop Chrome 138+).
// https://developer.chrome.com/docs/ai/translator-api
import { canonicalLang, toBcp47 } from '../shared/languages';
import type { Availability, Detection, PackProgress, TranslationEngine } from './types';

interface ChromeTranslator { translate(text: string): Promise<string>; destroy?(): void }
interface ChromeDetector { detect(text: string): Promise<{ detectedLanguage: string; confidence: number }[]> }
interface CreateMonitor { addEventListener(type: 'downloadprogress', cb: (e: { loaded: number; total?: number }) => void): void }
interface TranslatorStatic {
  availability(o: { sourceLanguage: string; targetLanguage: string }): Promise<string>;
  create(o: { sourceLanguage: string; targetLanguage: string; monitor?: (m: CreateMonitor) => void }): Promise<ChromeTranslator>;
}
interface DetectorStatic {
  availability(o?: object): Promise<string>;
  create(o?: { monitor?: (m: CreateMonitor) => void }): Promise<ChromeDetector>;
}

type G = typeof globalThis & { Translator?: TranslatorStatic; LanguageDetector?: DetectorStatic };

export function mapChromeAvailability(a: string): Availability {
  switch (a) {
    case 'available': case 'readily': return 'ready';
    case 'downloadable': case 'after-download': return 'downloadable';
    case 'downloading': return 'downloading';
    default: return 'unsupported';
  }
}

export class ChromeEngine implements TranslationEngine {
  readonly id = 'chrome';
  private translators = new Map<string, Promise<ChromeTranslator>>();
  /** Chrome runs translations sequentially per translator; we chain per pair so pairs never block each other. */
  private chains = new Map<string, Promise<unknown>>();
  private detector: Promise<ChromeDetector | null> | null = null;

  constructor(private g: G = globalThis as G) {}

  supported(): boolean { return typeof this.g.Translator?.availability === 'function'; }
  detectorSupported(): boolean { return typeof this.g.LanguageDetector?.create === 'function'; }

  async availability(from: string, to: string): Promise<Availability> {
    if (!this.supported() || from === to) return 'unsupported';
    try {
      return mapChromeAvailability(await this.g.Translator!.availability({ sourceLanguage: toBcp47(from), targetLanguage: toBcp47(to) }));
    } catch {
      return 'unsupported';
    }
  }

  async detectorAvailability(): Promise<Availability> {
    if (!this.detectorSupported()) return 'unsupported';
    try { return mapChromeAvailability(await this.g.LanguageDetector!.availability()); } catch { return 'unsupported'; }
  }

  /** Must run inside a user gesture the first time a pair downloads (Chrome's rule). */
  async ensurePack(from: string, to: string, onProgress?: (p: PackProgress) => void): Promise<void> {
    await this.translatorFor(from, to, onProgress);
  }

  async ensureDetector(onProgress?: (p: PackProgress) => void): Promise<void> {
    if (!this.detectorSupported()) throw new Error('Chrome LanguageDetector API not available');
    await this.g.LanguageDetector!.create({ monitor: (m) => m.addEventListener('downloadprogress', (e) => onProgress?.({ loaded: e.loaded, total: e.total ?? 1 })) });
  }

  private translatorFor(from: string, to: string, onProgress?: (p: PackProgress) => void): Promise<ChromeTranslator> {
    const key = `${from}-${to}`;
    let t = this.translators.get(key);
    if (!t) {
      t = this.g.Translator!.create({
        sourceLanguage: toBcp47(from),
        targetLanguage: toBcp47(to),
        monitor: (m) => m.addEventListener('downloadprogress', (e) => onProgress?.({ loaded: e.loaded, total: e.total ?? 1 })),
      });
      this.translators.set(key, t);
      t.catch(() => this.translators.delete(key));
    }
    return t;
  }

  async translate(texts: string[], from: string, to: string): Promise<string[]> {
    const key = `${from}-${to}`;
    const run = async (): Promise<string[]> => {
      const tr = await this.translatorFor(from, to);
      const out: string[] = [];
      for (const text of texts) out.push(await tr.translate(text));
      return out;
    };
    const prev = this.chains.get(key) ?? Promise.resolve();
    const next = prev.then(run, run);
    this.chains.set(key, next.catch(() => undefined));
    return next;
  }

  async detect(text: string): Promise<Detection | null> {
    if (!this.detectorSupported()) return null;
    if (!this.detector) {
      this.detector = (async () => {
        if ((await this.detectorAvailability()) !== 'ready') return null;
        return this.g.LanguageDetector!.create();
      })().catch(() => null);
    }
    const d = await this.detector;
    if (!d) { this.detector = null; return null; }
    const results = await d.detect(text);
    const top = results[0];
    if (!top || top.detectedLanguage === 'und') return null;
    return {
      lang: canonicalLang(top.detectedLanguage),
      confidence: top.confidence,
      alternatives: results.slice(1, 4).map((r) => ({ lang: canonicalLang(r.detectedLanguage), confidence: r.confidence })),
    };
  }

  dispose(): void {
    for (const t of this.translators.values()) void t.then((x) => x.destroy?.()).catch(() => undefined);
    this.translators.clear();
  }
}
