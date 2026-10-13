import { describe, expect, it } from 'vitest';
import { LRU } from '../../src/shared/lru';
import { PriorityQueue, Priority } from '../../src/shared/priorityQueue';
import { ChannelLanguageTracker } from '../../src/shared/channelLanguage';

describe('LRU', () => {
  it('evicts least recently used and refreshes on get', () => {
    const l = new LRU<string, number>(2);
    l.set('a', 1).set('b', 2);
    expect(l.get('a')).toBe(1);
    l.set('c', 3);
    expect(l.has('b')).toBe(false);
    expect([...l.keys()]).toEqual(['a', 'c']);
    l.resize(1);
    expect([...l.keys()]).toEqual(['c']);
  });
});

describe('PriorityQueue', () => {
  it('pops visible before nearby before history, FIFO within a class', () => {
    const q = new PriorityQueue<string>();
    q.push('h1', 'h1', Priority.history);
    q.push('n1', 'n1', Priority.nearby);
    q.push('v1', 'v1', Priority.visible);
    q.push('n2', 'n2', Priority.nearby);
    q.push('v2', 'v2', Priority.visible);
    expect(q.popMany(10).map((e) => e.key)).toEqual(['v1', 'v2', 'n1', 'n2', 'h1']);
  });
  it('re-push upgrades priority, cancel removes, reprioritize both ways', () => {
    const q = new PriorityQueue<number>();
    q.push('a', 1, 2); q.push('b', 2, 1); q.push('c', 3, 1);
    q.push('a', 1, 0);
    expect(q.pop()!.key).toBe('a');
    expect(q.cancel('b')).toBe(true);
    expect(q.cancel('b')).toBe(false);
    q.push('d', 4, 0);
    q.reprioritize('d', 5);
    expect(q.popMany(5).map((e) => e.key)).toEqual(['c', 'd']);
    expect(q.size).toBe(0);
  });
  it('stays a valid heap under random operations', () => {
    const q = new PriorityQueue<number>();
    const ref = new Map<string, number>();
    let seed = 7;
    const rnd = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
    for (let i = 0; i < 2000; i++) {
      const k = `k${Math.floor(rnd() * 200)}`;
      const r = rnd();
      if (r < 0.6) { const p = Math.floor(rnd() * 4); q.push(k, i, p); ref.set(k, Math.min(ref.get(k) ?? 99, p)); }
      else if (r < 0.8) { q.cancel(k); ref.delete(k); }
      else { const e = q.pop(); if (e) { expect(e.priority).toBe(Math.min(...ref.values())); ref.delete(e.key); } }
    }
    expect(q.size).toBe(ref.size);
  });
});

describe('channel language vote', () => {
  it('main language is the most frequent non-target language over a rolling window', () => {
    const t = new ChannelLanguageTracker(5, 0.5);
    ['fr', 'en', 'fr', 'es', 'en'].forEach((l) => t.add('c', l, 0.9));
    expect(t.main('c', 'en')).toBe('fr');
    ['es', 'es', 'es'].forEach((l) => t.add('c', l, 0.9));      // window rolls: fr drops out
    expect(t.main('c', 'en')).toBe('es');
    t.add('c', 'ja', 0.2);                                       // below confidence: ignored
    expect(t.tally('c').has('ja')).toBe(false);
    expect(t.main('other', 'en')).toBeNull();
  });
});
