import { useCallback, useEffect, useState } from 'react';
import {
  ApiError,
  getDocumentsStatus,
  searchDocuments,
  type DocumentSearchHit,
  type DocumentSearchPassage,
  type DocumentSearchResult,
  type DocumentsStatus,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { downloadUrl } from '../components/files/types';

// "Documenti" — search inside the owner's own documents.
//
// Retrieval only: no model writes anything here. Each result is a passage the
// server found in one of the caller's files, with WHERE it was found (PDF page,
// slide, sheet, section), so a person can check it in the document itself.
//
// The request carries the query and nothing else. Which library is searched is
// the signed-in account's, decided server-side; the one identifier that comes
// back is the caller's own file id, used here exactly as the file browser uses
// it — to open the file.
//
// Opening a document goes through the existing content endpoint, the same path
// the file browser uses for a non-media file. There is no in-app PDF viewer
// that accepts a page, so the page is shown as a badge rather than encoded into
// a link that would silently ignore it.
const MAX_QUERY = 400;

export function DocumentsPage() {
  const { invalidateAuth } = useAuth();
  const { t, tn } = useI18n();
  const [status, setStatus] = useState<DocumentsStatus | null>(null);
  const [query, setQuery] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<DocumentSearchResult | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const s = await getDocumentsStatus();
        if (!cancelled) setStatus(s);
      } catch (err) {
        if (err instanceof ApiError && err.status === 401) invalidateAuth();
        // The status is a courtesy line; search still works without it.
      }
    })();
    return () => { cancelled = true; };
  }, [invalidateAuth]);

  const search = useCallback(async () => {
    const text = query.trim();
    if (text.length === 0 || busy) return;
    setBusy(true);
    setError(null);
    try {
      setResult(await searchDocuments(text));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setResult(null);
      setError(t('documents.error'));
    } finally {
      setBusy(false);
    }
  }, [query, busy, t, invalidateAuth]);

  const indexing = status?.indexing;

  return (
    <section className="documents-page" aria-label={t('documents.title')}>
      <header className="documents-header">
        <h1>{t('documents.title')}</h1>
        <p className="muted">{t('documents.intro')}</p>
        {indexing && indexing.candidateFiles > 0 && (
          <p className="documents-indexing muted">
            {t('documents.indexing', {
              indexed: indexing.indexedFiles,
              total: indexing.candidateFiles,
            })}
          </p>
        )}
      </header>

      <form
        className="documents-search"
        role="search"
        onSubmit={(e) => { e.preventDefault(); void search(); }}
      >
        <label htmlFor="documents-query">{t('documents.queryLabel')}</label>
        <input
          id="documents-query"
          type="search"
          value={query}
          maxLength={MAX_QUERY}
          placeholder={t('documents.placeholder')}
          onChange={(e) => setQuery(e.target.value)}
        />
        <button type="submit" disabled={busy || query.trim().length === 0}>
          {busy ? t('documents.searching') : t('documents.search')}
        </button>
      </form>

      {error && <p className="error">{error}</p>}

      {result === null && !error && <p className="muted">{t('documents.empty')}</p>}

      {result && renderResults(result)}
    </section>
  );

  function renderResults(result: DocumentSearchResult) {
    if (result.status === 'not_indexed') {
      return <p className="muted">{t('documents.notIndexed')}</p>;
    }
    if (result.status === 'unavailable') {
      return <p className="muted">{t('documents.unavailable')}</p>;
    }

    return (
      <div className="documents-results" aria-live="polite">
        <p className="documents-mode muted">
          {modeLabel(result)}
          {result.visualUsed && <> · {t('documents.visualUsed')}</>}
        </p>
        {result.status !== 'ok' || result.documents.length === 0 ? (
          <p className="muted">{t('documents.noEvidence')}</p>
        ) : (
          <>
            <p className="documents-count muted">
              {tn(result.documents.length, 'documents.results')}
            </p>
            <ol className="documents-list">
              {result.documents.map((doc) => (
                renderDocument(doc)
              ))}
            </ol>
          </>
        )}
      </div>
    );
  }

  function renderDocument(doc: DocumentSearchHit) {
    return (
      <li key={`${doc.sourceKind}:${doc.sourceId}`} className="documents-result">
        <div className="documents-result-head">
          <h2 className="documents-result-name">{doc.name}</h2>
          {doc.sourceKind === 'file' && (
            <a
              className="documents-open"
              href={downloadUrl(doc.sourceId)}
              rel="noopener noreferrer"
              aria-label={t('documents.openLabel', { name: doc.name })}
            >
              {t('documents.open')}
            </a>
          )}
        </div>
        <ul className="documents-passages">
          {doc.passages.map((p) => (
            <li key={p.rank} className="documents-passage">
              <div className="documents-passage-meta">
                {locationBadges(p).map((badge) => (
                  <span key={badge} className="documents-badge">{badge}</span>
                ))}
                {p.section && <span className="documents-section">{p.section}</span>}
              </div>
              <p className="documents-snippet">{p.snippet}</p>
            </li>
          ))}
        </ul>
      </li>
    );
  }

  // Typed location → words. A real PDF page is "Pagina 3"; a slide, a sheet or
  // a Word section is said as itself, never as a page.
  function locationBadges(p: DocumentSearchPassage): string[] {
    const badges: string[] = [];
    if (p.page !== null) badges.push(t('documents.page', { n: p.page }));
    const loc = p.locator;
    if (!loc) return badges;
    if (loc.kind === 'slide') {
      const slide = t('documents.slide', { n: loc.index });
      badges.push(loc.label ? `${slide} — ${loc.label}` : slide);
    } else if (loc.kind === 'sheet') {
      badges.push(loc.label ?? t('documents.sheet', { n: loc.index }));
    } else if (loc.kind === 'section' && !p.section) {
      badges.push(loc.label ?? t('documents.section', { n: loc.index }));
    } else if (loc.kind === 'page' && p.page === null) {
      badges.push(t('documents.page', { n: loc.index }));
    }
    return badges;
  }

  function modeLabel(result: DocumentSearchResult): string {
    if (result.semantic) return t('documents.modeHybrid');
    if (result.fallbackReason) return t('documents.modeFallback');
    return t('documents.modeLexical');
  }
}
