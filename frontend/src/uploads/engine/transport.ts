// ONE REQUEST, WITH PROGRESS. The upload engine's only door to the network.
//
// XMLHttpRequest rather than fetch because fetch still cannot report upload
// progress in every browser people use, and a bar that does not move is the
// main reason somebody abandons a long upload.
//
// What a request can end in is deliberately small: a response (any status —
// classifying it is the engine's job, not the transport's) or one of three
// failures that carry no response at all.

export interface TransportRequest {
  method: 'GET' | 'POST' | 'PUT' | 'DELETE';
  url: string;
  // FormData for a multipart file, a Blob for a raw chunk, a string for JSON.
  body?: FormData | Blob | string;
  headers?: Record<string, string>;
  // Fraction of the REQUEST body sent so far, in bytes.
  onProgress?: (sentBytes: number) => void;
  signal?: AbortSignal;
}

export interface TransportResponse {
  status: number;
  body: unknown;
  // Parsed Retry-After, when the server sent one (429/503).
  retryAfterMs: number | null;
}

export type TransportFailure = 'network' | 'timeout' | 'aborted';

export class TransportError extends Error {
  readonly kind: TransportFailure;

  constructor(kind: TransportFailure) {
    super(`upload transport ${kind}`);
    this.name = 'TransportError';
    this.kind = kind;
  }
}

export type Transport = (request: TransportRequest) => Promise<TransportResponse>;

// Silence is bounded, not duration: a large chunk on a slow uplink may take
// minutes and must not be cut while bytes are still moving. Once the body has
// gone the server may be hashing, decoding or probing, so it gets longer.
const SENDING_IDLE_MS = 120_000;
const AWAITING_RESPONSE_MS = 300_000;

export function parseRetryAfter(raw: string | null, now = Date.now()): number | null {
  if (!raw) return null;
  const seconds = Number(raw);
  if (Number.isFinite(seconds) && seconds >= 0) return seconds * 1000;
  const at = Date.parse(raw);
  return Number.isNaN(at) ? null : Math.max(0, at - now);
}

export const xhrTransport: Transport = (request) => new Promise<TransportResponse>((resolve, reject) => {
  const { signal } = request;
  if (signal?.aborted) {
    reject(new TransportError('aborted'));
    return;
  }
  const xhr = new XMLHttpRequest();
  let settled = false;
  let timer: ReturnType<typeof setTimeout> | undefined;

  const finish = (outcome: TransportResponse | TransportError) => {
    if (settled) return;
    settled = true;
    if (timer !== undefined) clearTimeout(timer);
    signal?.removeEventListener('abort', onAbort);
    if (outcome instanceof TransportError) reject(outcome); else resolve(outcome);
  };
  const arm = (ms: number) => {
    if (timer !== undefined) clearTimeout(timer);
    timer = setTimeout(() => {
      finish(new TransportError('timeout'));
      xhr.abort();
    }, ms);
  };
  function onAbort() {
    finish(new TransportError('aborted'));
    xhr.abort();
  }

  xhr.open(request.method, request.url);
  xhr.withCredentials = true;
  for (const [name, value] of Object.entries(request.headers ?? {})) {
    xhr.setRequestHeader(name, value);
  }
  if (xhr.upload) {
    xhr.upload.onprogress = (event) => {
      if (settled) return;
      arm(SENDING_IDLE_MS);
      if (event.lengthComputable) request.onProgress?.(event.loaded);
    };
    xhr.upload.onload = () => {
      if (!settled) arm(AWAITING_RESPONSE_MS);
    };
  }
  xhr.onload = () => {
    const text = xhr.responseText;
    let body: unknown = null;
    if (text.length > 0) {
      try {
        body = JSON.parse(text);
      } catch {
        body = text;
      }
    }
    finish({
      status: xhr.status,
      body,
      retryAfterMs: parseRetryAfter(xhr.getResponseHeader('Retry-After')),
    });
  };
  xhr.onerror = () => finish(new TransportError('network'));
  xhr.ontimeout = () => finish(new TransportError('timeout'));
  xhr.onabort = () => finish(new TransportError('aborted'));
  signal?.addEventListener('abort', onAbort);

  arm(request.body === undefined ? AWAITING_RESPONSE_MS : SENDING_IDLE_MS);
  xhr.send(request.body ?? null);
});
