// Thin API client for seeding and for assertions that need the backend directly.
//
// The suite drives the product through the browser; this client exists only to
// build a known starting state and to read back facts the UI is asserted against.

import { API_URL } from './env';

export interface Session {
  readonly cookie: string;
  readonly email: string;
}

const json = { 'content-type': 'application/json' } as const;

/** Collect Set-Cookie into a single Cookie header value. */
const cookieHeader = (response: Response): string =>
  response.headers
    .getSetCookie()
    .map((raw) => raw.split(';', 1)[0])
    .join('; ');

/** Thrown for HTTP 429 so pollers can back off instead of failing. */
export class RateLimited extends Error {}

async function expectOk(response: Response, what: string): Promise<Response> {
  if (response.status === 429) {
    throw new RateLimited(`${what} was rate limited`);
  }
  if (!response.ok) {
    const body = await response.text().catch(() => '');
    throw new Error(
      `${what} failed: ${response.status} ${response.statusText}${body ? ` — ${body.slice(0, 400)}` : ''}`,
    );
  }
  return response;
}

export async function health(): Promise<boolean> {
  try {
    const response = await fetch(`${API_URL}/health`);
    return response.ok;
  } catch {
    return false;
  }
}

export async function login(email: string, password: string): Promise<Session> {
  const response = await expectOk(
    await fetch(`${API_URL}/api/auth/login`, {
      method: 'POST',
      headers: json,
      body: JSON.stringify({ email, password }),
    }),
    `login as ${email}`,
  );
  const cookie = cookieHeader(response);
  if (!cookie) throw new Error(`login as ${email} returned no session cookie`);
  return { cookie, email };
}

export async function get<T>(session: Session, path: string): Promise<T> {
  const response = await expectOk(
    await fetch(`${API_URL}${path}`, { headers: { cookie: session.cookie } }),
    `GET ${path}`,
  );
  return (await response.json()) as T;
}

/** GET without throwing — for asserting deliberate failures such as an invalid share. */
export async function getRaw(session: Session | null, path: string): Promise<Response> {
  return fetch(`${API_URL}${path}`, {
    headers: session ? { cookie: session.cookie } : {},
    redirect: 'manual',
  });
}

export async function post<T>(session: Session, path: string, body: unknown): Promise<T> {
  const response = await expectOk(
    await fetch(`${API_URL}${path}`, {
      method: 'POST',
      headers: { ...json, cookie: session.cookie },
      body: JSON.stringify(body),
    }),
    `POST ${path}`,
  );
  const text = await response.text();
  return (text ? JSON.parse(text) : null) as T;
}

export async function del(session: Session, path: string): Promise<void> {
  await expectOk(
    await fetch(`${API_URL}${path}`, { method: 'DELETE', headers: { cookie: session.cookie } }),
    `DELETE ${path}`,
  );
}

export interface UploadedFile {
  readonly id: string;
  readonly name: string;
}

/** Upload one file to the owner's root. The endpoint reads the `file` form field. */
export async function upload(
  session: Session,
  name: string,
  bytes: Uint8Array,
  contentType: string,
): Promise<UploadedFile> {
  const form = new FormData();
  form.append('file', new Blob([bytes as unknown as BlobPart], { type: contentType }), name);
  const response = await expectOk(
    await fetch(`${API_URL}/api/files`, {
      method: 'POST',
      headers: { cookie: session.cookie },
      body: form,
    }),
    `upload ${name}`,
  );
  const created = (await response.json()) as { id?: string; fileItemId?: string; name?: string };
  const id = created.id ?? created.fileItemId;
  if (!id) throw new Error(`upload ${name} returned no file id: ${JSON.stringify(created)}`);
  return { id, name: created.name ?? name };
}

export interface MediaItem {
  readonly fileItemId?: string;
  readonly id?: string;
  readonly name?: string;
  readonly kind?: string;
  readonly durationSeconds?: number | null;
}

export interface MediaPage {
  readonly items?: MediaItem[];
  readonly nextCursor?: string | null;
}

/**
 * One semantic evidence span. For a photo every timestamp is null; for a video
 * they locate the matching moment, which is what the marker strip renders.
 */
export interface SemanticMatch {
  readonly evidenceType?: string;
  readonly startMilliseconds: number | null;
  readonly endMilliseconds: number | null;
  readonly representativeMilliseconds: number | null;
}

/**
 * A semantic result. `bestMatch` plus `additionalMatches` are the markers: a
 * video with additional matches gets more than one, a photo gets none.
 */
export interface SemanticResult {
  readonly media: MediaItem;
  readonly bestMatch: SemanticMatch;
  readonly additionalMatches: SemanticMatch[];
}

/** Every marker for a result, in the order the strip should present them. */
export const markersOf = (result: SemanticResult): SemanticMatch[] =>
  [result.bestMatch, ...(result.additionalMatches ?? [])]
    .filter((m) => m.representativeMilliseconds !== null)
    .sort((a, b) => (a.representativeMilliseconds ?? 0) - (b.representativeMilliseconds ?? 0));

export const mediaItemId = (item: MediaItem): string => {
  const id = item.fileItemId ?? item.id;
  if (!id) throw new Error(`media item has no id: ${JSON.stringify(item)}`);
  return id;
};

export async function media(
  session: Session,
  query: Record<string, string | number | undefined> = {},
): Promise<MediaPage> {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined) search.set(key, String(value));
  }
  return get<MediaPage>(session, `/api/media?${search.toString()}`);
}

export async function semantic(
  session: Session,
  query: Record<string, string | number | undefined>,
): Promise<{ items?: SemanticResult[]; nextCursor?: string | null }> {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined) search.set(key, String(value));
  }
  return get(session, `/api/media/semantic?${search.toString()}`);
}

/** Poll until `predicate` holds or the budget runs out. Deterministic AI still
 *  runs as background jobs, so seeding has to wait for the worker to catch up. */
export async function waitFor(
  what: string,
  predicate: () => Promise<boolean>,
  timeoutMs: number,
  intervalMs = 1000,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let lastError: unknown;
  let wait = intervalMs;
  while (Date.now() < deadline) {
    try {
      if (await predicate()) return;
      lastError = undefined;
      wait = intervalMs;
    } catch (error) {
      lastError = error;
      // Being rate limited says nothing about readiness — back off and retry
      // rather than treating the limiter's answer as a failure.
      if (error instanceof RateLimited) {
        wait = Math.min(wait * 2, 15_000);
      }
    }
    await new Promise((resolve) => setTimeout(resolve, wait));
  }
  throw new Error(
    `timed out after ${timeoutMs} ms waiting for ${what}` +
      (lastError ? `; last error: ${String(lastError)}` : ''),
  );
}
