/** Controllable upload transport: keep requests pending until a test confirms them. */
export class MockUploadXhr {
  static sent: MockUploadXhr[] = [];
  method = '';
  url = '';
  body: FormData | null = null;
  withCredentials = false;
  status = 0;
  responseText = '';
  aborted = false;
  upload: {
    onprogress?: (event: { lengthComputable: boolean; loaded: number; total: number }) => void;
    onload?: () => void;
  } = {};
  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;
  onabort: (() => void) | null = null;
  ontimeout: (() => void) | null = null;
  open(method: string, url: string) { this.method = method; this.url = url; }
  send(body: FormData) { this.body = body; MockUploadXhr.sent.push(this); }
  abort() { this.aborted = true; this.onabort?.(); }
  progress(loaded: number, total: number, lengthComputable = true) {
    this.upload.onprogress?.({ lengthComputable, loaded, total });
  }
  finish(report: unknown = { accepted: 1, rejected: 0, stopped: null }, status = 200) {
    this.status = status;
    this.responseText = typeof report === 'string' ? report : JSON.stringify(report);
    this.onload?.();
  }
}
