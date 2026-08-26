// Production transport for sync originals: the EXISTING owner ingestion
// endpoint (POST /api/files) through the ONE shared API client.
//
// Why fetch + FormData: a part built as { uri, name, type } is streamed by
// the native networking stack straight from disk — original bytes never pass
// through (and never sit inside) the JS heap, and nothing is ever base64
// encoded. Auth rides the same session-cookie seam every other call uses;
// the Idempotency-Key header carries the OPERATION identity so an ambiguous
// retry can never create a second logical ingestion.

import { apiRequest } from '../api/client.ts';
import type { UploadRequest, UploadedFile } from './syncTypes.ts';

export function uploadAssetViaOwnerEndpoint(request: UploadRequest): Promise<UploadedFile> {
  // RN's FormData file part: uri is the platform file URI from the media
  // library (file:// on both platforms we support). No temporary copy is
  // needed, so no temp-file lifecycle exists to get wrong.
  const form = new FormData();
  form.append('file', {
    uri: request.localUri,
    name: request.filename,
    type: request.mimeType,
  } as unknown as Blob);

  return apiRequest<UploadedFile>('POST', '/api/files', {
    form,
    headers: { 'Idempotency-Key': request.operationKey },
    signal: request.signal,
    timeoutMs: request.timeoutMs,
  });
}
