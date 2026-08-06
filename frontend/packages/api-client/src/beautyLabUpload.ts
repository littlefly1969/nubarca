import { api } from './client';

// PUBLIC, unauthenticated TV "Beauty Lab" (Laboratorio bellezza) mobile upload.
// Reached by scanning the QR shown on the paired TV. The :token is a short-lived
// capability that ONLY uploads images into the owner's Aesthetics Lab — it can
// never list, read, analyze, or delete, and exposes NO owner identity or lab
// contents. When the session is unknown/expired/revoked the API answers 404 (for
// the raw upload) or reports the lifecycle state (for the state read) so the page
// can show a clear message. No auth cookie or personal grant is involved.

// Coarse, safe lifecycle the page renders. Mirrors the backend states.
export type BeautyLabUploadStatus = 'active' | 'full' | 'expired' | 'revoked';

export interface BeautyLabUploadState {
  status: BeautyLabUploadStatus;
  expiresAt: string;
  maxFiles: number;
  maxTotalBytes: number;
  accepted: number;
  rejected: number;
}

export interface BeautyLabUploadFileResult {
  name: string;
  ok: boolean;
  reason: string | null;
}

export interface BeautyLabUploadResult {
  accepted: number;
  rejected: number;
  status: BeautyLabUploadStatus;
  files: BeautyLabUploadFileResult[];
}

// Reads the session lifecycle/progress by token. 404 (ApiError) ⇒ unknown token.
export function getBeautyLabUploadState(
  token: string,
  signal?: AbortSignal,
): Promise<BeautyLabUploadState> {
  return api<BeautyLabUploadState>(
    `/api/beauty-lab-upload/${encodeURIComponent(token)}`,
    { signal },
  );
}

// Uploads one or more images straight into the lab via the capability token.
export function uploadBeautyLabFiles(
  token: string,
  files: File[],
  signal?: AbortSignal,
): Promise<BeautyLabUploadResult> {
  const form = new FormData();
  for (const file of files) {
    form.append('file', file, file.name);
  }
  return api<BeautyLabUploadResult>(
    `/api/beauty-lab-upload/${encodeURIComponent(token)}/files`,
    { method: 'POST', formData: form, signal },
  );
}
