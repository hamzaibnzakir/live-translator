// Deterministic engine for logic tests: a tiny phrase table plus a visible "[from→to]" fallback.
import type { Availability, TranslationEngine } from './types';

export const FAKE_PHRASES: Record<string, string> = {
  'fr|Bonjour tout le monde': 'Hello everyone',
  'fr|Salut': 'Hi',
  'fr|comment ça va ?': 'how are you?',
  'ja|こんにちは': 'Hello',
  'en|Hello': 'Bonjour',
};

export class FakeEngine implements TranslationEngine {
  readonly id = 'fake';
  calls: { texts: string[]; from: string; to: string }[] = [];
  installed = new Set<string>();
  constructor(private opts: { delayMs?: number; pairs?: string[]; installAll?: boolean } = {}) {}

  supported(): boolean { return true; }

  async availability(from: string, to: string): Promise<Availability> {
    const pair = `${from}-${to}`;
    if (this.opts.pairs && !this.opts.pairs.includes(pair)) return 'unsupported';
    return this.opts.installAll || this.installed.has(pair) ? 'ready' : 'downloadable';
  }

  async ensurePack(from: string, to: string, onProgress?: (p: { loaded: number; total: number }) => void): Promise<void> {
    onProgress?.({ loaded: 1, total: 1 });
    this.installed.add(`${from}-${to}`);
  }

  async translate(texts: string[], from: string, to: string): Promise<string[]> {
    this.calls.push({ texts, from, to });
    if (this.opts.delayMs) await new Promise((r) => setTimeout(r, this.opts.delayMs));
    return texts.map((t) => FAKE_PHRASES[`${from}|${t}`] ?? `[${from}→${to}] ${t}`);
  }
}
