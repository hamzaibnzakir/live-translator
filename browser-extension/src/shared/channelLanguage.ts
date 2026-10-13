/**
 * Rolling vote over the last N confidently detected messages of a channel. The channel's
 * "main language" is the most frequent language other than the reader's own.
 */
export class ChannelLanguageTracker {
  private votes = new Map<string, { lang: string; weight: number }[]>();
  constructor(private windowSize = 30, private minConfidence = 0.5) {}

  add(channel: string, lang: string, confidence = 1): void {
    if (!lang || lang === 'und' || confidence < this.minConfidence) return;
    const list = this.votes.get(channel) ?? [];
    list.push({ lang, weight: confidence });
    if (list.length > this.windowSize) list.splice(0, list.length - this.windowSize);
    this.votes.set(channel, list);
  }

  tally(channel: string): Map<string, number> {
    const t = new Map<string, number>();
    for (const v of this.votes.get(channel) ?? []) t.set(v.lang, (t.get(v.lang) ?? 0) + v.weight);
    return t;
  }

  /** Main language excluding `exclude` (normally the reader's target language), or null if unknown. */
  main(channel: string, exclude?: string): string | null {
    let best: string | null = null, bestScore = 0;
    for (const [lang, score] of this.tally(channel)) {
      if (lang === exclude) continue;
      if (score > bestScore) { best = lang; bestScore = score; }
    }
    return best;
  }

  reset(channel?: string): void { if (channel) this.votes.delete(channel); else this.votes.clear(); }
}
