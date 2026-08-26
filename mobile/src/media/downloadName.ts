// Pure helpers for the shared-album ORIGINAL download: derive the on-disk
// name and MIME from what the server actually said (Content-Disposition +
// Content-Type), instead of guessing an extension from the media kind.
// A HEIC stays .heic, a MOV stays .mov — the original is served byte-exact
// with its own metadata, and the saved file must match it.

export type HeaderBag = Record<string, string | string[] | undefined>;

/** Case-insensitive header lookup over whatever bag expo hands back. */
export function pickHeader(headers: HeaderBag, name: string): string | null {
  const lower = name.toLowerCase();
  for (const key of Object.keys(headers)) {
    if (key.toLowerCase() !== lower) continue;
    const value = headers[key];
    if (Array.isArray(value)) return value[0] ?? null;
    return value ?? null;
  }
  return null;
}

/**
 * Extract the attachment filename from a Content-Disposition header.
 * Handles both RFC 5987 (`filename*=UTF-8''na%C3%AFve.jpg`) and the classic
 * quoted form, preferring the extended one. Returns null when absent.
 */
export function parseAttachmentFilename(disposition: string | null): string | null {
  if (!disposition) return null;
  const extended = /filename\*\s*=\s*(?:utf-8|iso-8859-1)'([^']*)'([^;]+)/i.exec(disposition);
  if (extended) {
    const charset = extended[1].toLowerCase() === 'iso-8859-1' ? 'latin1' : 'utf8';
    void charset; // Node/RN decodeURIComponent covers UTF-8; latin1 passes through
    try {
      const name = decodeURIComponent(extended[2].trim());
      return sanitize(name);
    } catch {
      /* fall through to the plain form */
    }
  }
  const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(disposition);
  return plain ? sanitize(plain[1].trim()) : null;
}

function sanitize(name: string): string {
  // Strip any path component: a server value must never escape the target dir.
  const base = name.split(/[\\/]/).pop()?.trim() ?? '';
  return base.length > 0 && base !== '.' && base !== '..' ? base : '';
}

const MIME_EXTENSIONS: Array<[RegExp, string]> = [
  [/jpe?g/i, 'jpg'],
  [/png/i, 'png'],
  [/heic|heif/i, 'heic'],
  [/webp/i, 'webp'],
  [/gif/i, 'gif'],
  [/mp4|m4v/i, 'mp4'],
  [/quicktime/i, 'mov'],
  [/matroska|webm/i, 'webm'],
  [/x-msvideo|avi/i, 'avi'],
];

function extensionForMime(mime: string): string | null {
  for (const [pattern, extension] of MIME_EXTENSIONS) {
    if (pattern.test(mime)) return extension;
  }
  return null;
}

/**
 * Final save name: prefer the server's original filename (adding no second
 * extension when it already carries one); otherwise build from the MIME;
 * otherwise fall back to the media kind.
 */
export function buildDownloadName(options: {
  disposition: string | null;
  mimeType: string | null;
  kindFallbackExtension: string;
}): string {
  const { disposition, mimeType, kindFallbackExtension } = options;

  const serverName = parseAttachmentFilename(disposition);
  if (serverName) {
    if (/\.[A-Za-z0-9]{2,5}$/.test(serverName)) return serverName;
    const fromMime = mimeType ? extensionForMime(mimeType) : null;
    return `${serverName}.${fromMime ?? kindFallbackExtension}`;
  }

  const fromMime = mimeType ? extensionForMime(mimeType) : null;
  if (fromMime) return `nubarca-original.${fromMime}`;
  return `nubarca-original.${kindFallbackExtension}`;
}
