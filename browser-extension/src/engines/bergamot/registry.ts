import pinned from './registry.pinned.json';

export interface PinnedFile { path: string; sha256: string; size: number; uncompressedSha256?: string; uncompressedSize?: number }
export interface PinnedPair {
  from: string;
  to: string;
  architecture: string;
  releaseStatus: string | null;
  files: Partial<Record<'model' | 'lexicalShortlist' | 'vocab' | 'srcVocab' | 'trgVocab', PinnedFile>>;
}
export interface PinnedRegistry { source: string; baseUrl: string; registryGenerated: string; pinnedAt: string; pairs: Record<string, PinnedPair> }

export const REGISTRY = pinned as unknown as PinnedRegistry;
export const DEFAULT_PACK_BASE_URL = REGISTRY.baseUrl;
export const PIVOT = 'en';

export function pairKey(from: string, to: string): string { return `${from}-${to}`; }

/** Model pairs needed for from→to: direct when English is on one side, otherwise pivot through English. */
export function modelsFor(from: string, to: string, registry: PinnedRegistry = REGISTRY): string[] | null {
  if (from === to) return null;
  const direct = pairKey(from, to);
  if (registry.pairs[direct]) return [direct];
  if (from !== PIVOT && to !== PIVOT) {
    const a = pairKey(from, PIVOT), b = pairKey(PIVOT, to);
    if (registry.pairs[a] && registry.pairs[b]) return [a, b];
  }
  return null;
}

export function pairDownloadSize(pair: string, registry: PinnedRegistry = REGISTRY): number {
  const p = registry.pairs[pair];
  return p ? Object.values(p.files).reduce((s, f) => s + (f?.size ?? 0), 0) : 0;
}

export function pairInstalledSize(pair: string, registry: PinnedRegistry = REGISTRY): number {
  const p = registry.pairs[pair];
  return p ? Object.values(p.files).reduce((s, f) => s + (f?.uncompressedSize ?? f?.size ?? 0), 0) : 0;
}
