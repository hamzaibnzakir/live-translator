// Remembers, per Text node, the original value and the value we wrote. A node whose current
// data differs from what we wrote has been changed by the page (e.g. a Discord edit).
export class TextRewriter {
  private original = new WeakMap<Text, string>();
  private written = new WeakMap<Text, string>();
  readonly touched = new Set<Text>();

  isOurs(n: Text): boolean { return this.written.get(n) === n.data; }

  /** Original text if the node currently shows our translation, else undefined (node is original). */
  originalOf(n: Text): string | undefined { return this.isOurs(n) ? this.original.get(n) : undefined; }

  write(n: Text, value: string): void {
    if (!this.isOurs(n)) this.original.set(n, n.data);
    if (n.data !== value) n.data = value;
    this.written.set(n, value);
    this.touched.add(n);
  }

  restore(n: Text): void {
    if (!this.isOurs(n)) { this.touched.delete(n); return; }
    const o = this.original.get(n);
    this.written.delete(n);
    this.touched.delete(n);
    if (o !== undefined && n.data !== o) n.data = o;
  }

  restoreAll(): number {
    let n = 0;
    for (const t of [...this.touched]) { if (this.isOurs(t)) n++; this.restore(t); }
    this.touched.clear();
    return n;
  }

  /** Forget nodes that left the document (keeps `touched` small on long sessions). */
  sweep(): void { for (const t of this.touched) if (!t.isConnected) this.touched.delete(t); }
}
