// RPC client for Mozilla's bergamot `translator-worker.js` protocol: {id, name, args} → {id, result|error}.

export interface WorkerLike {
  postMessage(msg: unknown, transfer?: Transferable[]): void;
  addEventListener(type: 'message', cb: (e: { data: unknown }) => void): void;
  addEventListener(type: 'error', cb: (e: unknown) => void): void;
  terminate(): void;
}

interface Reply { id: number; result?: unknown; error?: { name?: string; message?: string } }

export class WorkerClient {
  private seq = 0;
  private pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
  private dead: Error | null = null;

  constructor(private worker: WorkerLike) {
    worker.addEventListener('message', (e) => {
      const msg = e.data as Reply;
      const p = this.pending.get(msg.id);
      if (!p) return;
      this.pending.delete(msg.id);
      if (msg.error) p.reject(new Error(`${msg.error.name ?? 'Error'}: ${msg.error.message ?? 'worker error'}`));
      else p.resolve(msg.result);
    });
    worker.addEventListener('error', (e) => this.fail(new Error(`Bergamot worker crashed: ${String((e as { message?: string }).message ?? e)}`)));
  }

  call<T>(name: string, args: unknown[] = [], transfer: Transferable[] = []): Promise<T> {
    if (this.dead) return Promise.reject(this.dead);
    const id = ++this.seq;
    return new Promise<T>((resolve, reject) => {
      this.pending.set(id, { resolve: resolve as (v: unknown) => void, reject });
      this.worker.postMessage({ id, name, args }, transfer);
    });
  }

  private fail(err: Error): void {
    this.dead = err;
    for (const p of this.pending.values()) p.reject(err);
    this.pending.clear();
  }

  terminate(): void {
    this.fail(new Error('worker terminated'));
    this.worker.terminate();
  }
}
