// Text-node walker. Collects the text of a block as "segments": runs of text nodes that belong
// together. Protected elements (mentions, emoji, code, links in Discord…) are skipped entirely
// and act as boundaries. We only ever change Text.data, never move or create elements, so
// frameworks such as React that own the DOM keep working.

import { splitWhitespace } from './normalize';

export interface Segment {
  nodes: Text[];
  /** The original text of the segment (all nodes joined). */
  text: string;
}

export interface WalkOptions {
  /** Element is skipped with all its descendants (code, mentions, emoji…). */
  skip: (el: Element) => boolean;
  /** Element starts a new segment and its own text forms its own segment (links/buttons on web pages). */
  boundary?: (el: Element) => boolean;
  /** Original text for a node we already translated (so re-walking a translated block works). */
  originalOf?: (node: Text) => string | undefined;
}

const ELEMENT = 1, TEXT = 3;

/** Inline formatting that does not break a sentence apart. */
export const INLINE_TAGS = new Set(['B', 'STRONG', 'I', 'EM', 'U', 'S', 'DEL', 'INS', 'MARK', 'SMALL', 'SUB', 'SUP', 'SPAN', 'FONT', 'ABBR', 'CITE', 'Q', 'BDI', 'BDO', 'DFN', 'TIME', 'DATA', 'WBR']);

export function collectSegments(root: Element, opts: WalkOptions): Segment[] {
  const segments: Segment[] = [];
  let current: Text[] = [];
  const flush = (): void => {
    if (current.length) {
      const text = current.map((n) => opts.originalOf?.(n) ?? n.data).join('');
      if (text.trim()) segments.push({ nodes: current, text });
      current = [];
    }
  };
  const visit = (node: Node): void => {
    for (let child = node.firstChild; child; child = child.nextSibling) {
      if (child.nodeType === TEXT) {
        current.push(child as Text);
      } else if (child.nodeType === ELEMENT) {
        const el = child as Element;
        if (opts.skip(el)) { flush(); continue; }
        const isBoundary = !INLINE_TAGS.has(el.tagName) || (opts.boundary?.(el) ?? false);
        if (isBoundary) flush();
        visit(el);
        if (isBoundary) flush();
      }
    }
  };
  visit(root);
  flush();
  return segments;
}

/** Joined text of all segments, for language detection and change detection. */
export function blockText(segments: Segment[]): string {
  return segments.map((s) => s.text.trim()).join('\n');
}

/**
 * Writes a translation into a segment: the first node gets the translated text (keeping the
 * original's leading/trailing whitespace), the rest are emptied. Returns the written values.
 */
export function writeSegment(seg: Segment, translation: string, write: (node: Text, value: string) => void): void {
  const [lead, core, trail] = splitWhitespace(seg.text);
  let t = matchLeadingCase(core, translation.trim());
  // A segment that continues after a mention/emoji often starts with a separator (", tu viens…").
  // Engines sometimes drop it; put it back so words never glue together ("@David, you…").
  const sep = /^[,;:.…)\]»–—-]+\s*/u.exec(core)?.[0];
  if (sep && !t.startsWith(sep.trim())) t = sep + t;
  seg.nodes.forEach((n, i) => write(n, i === 0 ? lead + t + trail : ''));
}

/**
 * MT models capitalise every input. A segment that continues a sentence (" et installe …")
 * started lower-case, so its translation should too ("…and install … before tomorrow").
 */
export function matchLeadingCase(source: string, translation: string): string {
  const s = /\p{L}/u.exec(source)?.[0];
  const m = /\p{L}/u.exec(translation);
  if (!s || !m) return translation;
  const t = m[0];
  const next = translation[m.index + 1] ?? '';
  const sourceLower = s === s.toLowerCase() && s !== s.toUpperCase();
  const isAcronymOrI = (t === 'I' && !/\p{L}/u.test(next)) || (next !== '' && next === next.toUpperCase() && next !== next.toLowerCase());
  if (sourceLower && t !== t.toLowerCase() && !isAcronymOrI) return translation.slice(0, m.index) + t.toLowerCase() + translation.slice(m.index + 1);
  return translation;
}
