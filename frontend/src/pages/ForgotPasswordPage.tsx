import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { Link } from 'react-router';
import { ApiError, fetchPasswordRecoveryStatus, requestPasswordRecovery } from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { BrandMark } from '../brand/BrandMark';

// PUBLIC. The person asking cannot sign in, so this page is unauthenticated by
// necessity.
//
// The completion state is the same sentence whatever happened server-side — a
// real address, an unknown one, a disabled account, a mail server that refused.
// The page never branches on the answer, because there is no answer to branch
// on: the API returns one generic acceptance for every case.
export function ForgotPasswordPage() {
  const { t } = useI18n();
  const [email, setEmail] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [done, setDone] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // null while the probe is in flight, so the page does not flash the wrong copy.
  const [recoveryEnabled, setRecoveryEnabled] = useState<boolean | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    fetchPasswordRecoveryStatus(controller.signal)
      .then((status) => setRecoveryEnabled(status.enabled))
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        // Unreachable status means we cannot promise delivery, so present the
        // unavailable copy rather than a form that silently does nothing.
        setRecoveryEnabled(false);
      });
    return () => controller.abort();
  }, []);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    try {
      await requestPasswordRecovery(email);
      setDone(true);
    } catch (err) {
      // 429 is the only outcome worth telling the user about, and it says
      // "you asked too often" — never anything about the account.
      if (err instanceof ApiError && err.status === 429) {
        setError(t('recovery.tooManyRequests'));
      } else {
        setError(t('recovery.unavailable'));
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="login-page">
      <div className="login-card">
        <h1 className="login-title">
          <BrandMark variant="wordmark" size={200} />
        </h1>
        <h2 id="forgot-title">{t('recovery.forgotHeading')}</h2>

        {recoveryEnabled === false && (
          <p role="status" data-testid="recovery-disabled">
            {t('recovery.disabledExplanation')}
          </p>
        )}

        {done ? (
          // One sentence, always the same one.
          <p role="status" data-testid="recovery-sent">{t('recovery.genericSent')}</p>
        ) : (
          recoveryEnabled !== false && (
            <form onSubmit={onSubmit} aria-labelledby="forgot-title">
              <p>{t('recovery.forgotIntro')}</p>

              <label htmlFor="recovery-email">{t('login.email')}</label>
              <input
                id="recovery-email"
                type="email"
                autoComplete="email"
                required
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                disabled={submitting}
              />

              <button type="submit" disabled={submitting}>
                {submitting ? t('recovery.sending') : t('recovery.sendLink')}
              </button>

              {error !== null && (
                <div className="login-error" role="alert">{error}</div>
              )}
            </form>
          )
        )}

        <p>
          <Link to="/login">{t('recovery.backToLogin')}</Link>
        </p>
      </div>
    </div>
  );
}
