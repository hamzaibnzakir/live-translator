export type Availability = 'ready' | 'downloadable' | 'downloading' | 'unsupported';

export interface Detection {
  lang: string;
  confidence: number;
  /** Runner-up candidates, most likely first. */
  alternatives?: { lang: string; confidence: number }[];
}

export interface PackProgress { loaded: number; total: number }

/** The contract every translation backend implements. Language codes are canonical (see languages.ts). */
export interface TranslationEngine {
  readonly id: 'chrome' | 'bergamot' | 'fake' | string;
  /** Whether the engine exists in this browser at all. */
  supported(): boolean;
  detect?(text: string): Promise<Detection | null>;
  availability(from: string, to: string): Promise<Availability>;
  ensurePack(from: string, to: string, onProgress?: (p: PackProgress) => void): Promise<void>;
  /** Translates a batch; results are in input order. */
  translate(texts: string[], from: string, to: string): Promise<string[]>;
  removePack?(from: string, to: string): Promise<void>;
  /** Approximate download size of the pack(s) needed for a pair. */
  packSize?(from: string, to: string): number;
  stats?(): { pair: string; wasmBytes: number }[];
  dispose?(): void;
}

export class NeedsPackError extends Error {
  constructor(public from: string, public to: string, public engine: string) {
    super(`Language pack ${from}→${to} (${engine}) is not installed`);
  }
}
