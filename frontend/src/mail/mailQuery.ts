// The MailArchive search filters, in their two spellings.
//
// The ADDRESS BAR carries them in a readable form (`since` / `until` as plain
// calendar dates, `until` inclusive) so a search is linkable and Back/Forward
// moves between searches. The API takes `after` (inclusive) and `before`
// (exclusive) as ISO instants (contract §5). The conversion lives here and
// nowhere else, so both views build exactly the same request.

export interface MailFilters {
  archiveId: string | null;
  folderId: string | null;
  q: string;
  from: string;
  to: string;
  subject: string;
  // YYYY-MM-DD, inclusive at both ends.
  since: string;
  until: string;
  hasAttachments: boolean;
}

export type MailView = 'mail' | 'documents' | 'imports';

export const EMPTY_FILTERS: MailFilters = {
  archiveId: null,
  folderId: null,
  q: '',
  from: '',
  to: '',
  subject: '',
  since: '',
  until: '',
  hasAttachments: false,
};

const DATE = /^\d{4}-\d{2}-\d{2}$/;

function text(params: URLSearchParams, key: string): string {
  return (params.get(key) ?? '').trim();
}

function id(params: URLSearchParams, key: string): string | null {
  const value = text(params, key);
  return value.length > 0 ? value : null;
}

function date(params: URLSearchParams, key: string): string {
  const value = text(params, key);
  return DATE.test(value) ? value : '';
}

export function readView(params: URLSearchParams): MailView {
  const v = params.get('view');
  return v === 'documents' || v === 'imports' ? v : 'mail';
}

export function readFilters(params: URLSearchParams): MailFilters {
  return {
    archiveId: id(params, 'archive'),
    folderId: id(params, 'folder'),
    q: text(params, 'q'),
    from: text(params, 'from'),
    to: text(params, 'to'),
    subject: text(params, 'subject'),
    since: date(params, 'since'),
    until: date(params, 'until'),
    hasAttachments: params.get('attachments') === '1',
  };
}

// Address-bar form. Empty filters are omitted so a plain folder view has a
// short, stable URL.
export function filtersToParams(view: MailView, filters: MailFilters): URLSearchParams {
  const out = new URLSearchParams();
  if (view !== 'mail') out.set('view', view);
  if (filters.archiveId) out.set('archive', filters.archiveId);
  if (filters.folderId) out.set('folder', filters.folderId);
  if (filters.q) out.set('q', filters.q);
  if (filters.from) out.set('from', filters.from);
  if (filters.to) out.set('to', filters.to);
  if (filters.subject) out.set('subject', filters.subject);
  if (filters.since) out.set('since', filters.since);
  if (filters.until) out.set('until', filters.until);
  if (filters.hasAttachments) out.set('attachments', '1');
  return out;
}

// The START of a calendar day in the viewer's own time zone, as an ISO instant:
// "messages of 1 March" means 1 March where the owner lives, not in UTC.
export function localDayStartIso(day: string, addDays = 0): string {
  const [y, m, d] = day.split('-').map(Number);
  return new Date(y, m - 1, d + addDays).toISOString();
}

export function hasSearchCriteria(filters: MailFilters): boolean {
  return Boolean(
    filters.q || filters.from || filters.to || filters.subject ||
      filters.since || filters.until || filters.hasAttachments,
  );
}

// API form: the query string for GET /api/mail/messages (without cursor).
export function filtersToApiQuery(filters: MailFilters, limit = 50): URLSearchParams {
  const out = new URLSearchParams();
  if (filters.archiveId) out.set('archiveId', filters.archiveId);
  if (filters.folderId) out.set('folderId', filters.folderId);
  if (filters.q) out.set('q', filters.q);
  if (filters.from) out.set('from', filters.from);
  if (filters.to) out.set('to', filters.to);
  if (filters.subject) out.set('subject', filters.subject);
  if (filters.since) out.set('after', localDayStartIso(filters.since));
  // `until` is inclusive for the person; `before` is exclusive for the API.
  if (filters.until) out.set('before', localDayStartIso(filters.until, 1));
  if (filters.hasAttachments) out.set('hasAttachments', 'true');
  out.set('limit', String(limit));
  return out;
}
