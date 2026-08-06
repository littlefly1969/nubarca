// Video-hls slice 4: pure classification of the /video probe answer (no React,
// no native imports) — covered by node --test. The fetch wrapper lives in
// probe.ts; the contract itself is documented there.

export type TvVideoMode = 'hls' | 'direct' | 'preparing' | 'error';

export function classifyVideoProbe(status: number, contentType: string | null): TvVideoMode {
  if (status === 202) return 'preparing';
  if (status !== 200 && status !== 206) return 'error';
  return (contentType ?? '').toLowerCase().includes('mpegurl') ? 'hls' : 'direct';
}
