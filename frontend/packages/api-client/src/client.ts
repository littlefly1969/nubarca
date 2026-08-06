// Thin wrapper around fetch that:
//   * always sends the auth cookie via `credentials: 'include'`;
//   * accepts/returns JSON with sensible defaults;
//   * normalises errors so call sites only have to handle `ApiError`.
//
// We never set Authorization headers or read/write tokens from JS — the
// auth cookie is HttpOnly and managed entirely by the browser.

export class ApiError extends Error {
  readonly status: number;
  readonly body: unknown;

  constructor(status: number, message: string, body: unknown = null) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.body = body;
  }
}

export interface ApiRequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  json?: unknown;
  // When set, the request is sent as multipart/form-data. We deliberately
  // do NOT set content-type ourselves — the browser fills it in with the
  // boundary parameter, which is required for the server to parse the body.
  formData?: FormData;
  // Slice 93: raw request body (chunk uploads). Sent as octet-stream.
  rawBody?: Blob | ArrayBuffer;
  // Extra request headers (e.g. the Private Vault unlock token). Merged over
  // the defaults. Never used for the auth cookie (that stays HttpOnly).
  headers?: Record<string, string>;
  signal?: AbortSignal;
}

export async function api<T = unknown>(
  path: string,
  options: ApiRequestOptions = {},
): Promise<T> {
  const { method = 'GET', json, formData, rawBody, signal } = options;
  const headers: Record<string, string> = {};
  let body: BodyInit | undefined;
  if (json !== undefined) {
    headers['content-type'] = 'application/json';
    body = JSON.stringify(json);
  } else if (formData !== undefined) {
    body = formData;
  } else if (rawBody !== undefined) {
    headers['content-type'] = 'application/octet-stream';
    body = rawBody;
  }
  if (options.headers) {
    for (const [k, v] of Object.entries(options.headers)) {
      headers[k] = v;
    }
  }

  const response = await fetch(path, {
    method,
    headers,
    body,
    signal,
    credentials: 'include',
  });

  if (response.status === 204) {
    return undefined as T;
  }

  const text = await response.text();
  let parsed: unknown = null;
  if (text.length > 0) {
    try {
      parsed = JSON.parse(text);
    } catch {
      parsed = text;
    }
  }

  if (!response.ok) {
    throw new ApiError(
      response.status,
      `Request failed: ${method} ${path} → ${response.status}`,
      parsed,
    );
  }

  return parsed as T;
}
