import { useState } from 'react';
import type { FormEvent } from 'react';
import { Navigate, useLocation } from 'react-router';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { BrandMark } from '../brand/BrandMark';

export function LoginPage() {
  const { state, login } = useAuth();
  const { t } = useI18n();
  const location = useLocation();
  const requestedReturn = (location.state as { returnTo?: unknown } | null)?.returnTo;
  const returnTo = typeof requestedReturn === 'string'
    && requestedReturn.startsWith('/')
    && !requestedReturn.startsWith('//')
    ? requestedReturn
    : '/';
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (state.status === 'authed') {
    return <Navigate to={returnTo} replace />;
  }

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    try {
      await login(email, password);
    } catch (err) {
      setError(err instanceof Error ? err.message : t('login.failed'));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="login-page">
      <form className="login-card" onSubmit={onSubmit} aria-labelledby="login-title">
        <h1 id="login-title" className="login-title">
          <BrandMark variant="wordmark" size={200} />
        </h1>

        <label htmlFor="email">{t('login.email')}</label>
        <input
          id="email"
          type="email"
          autoComplete="email"
          required
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          disabled={submitting}
        />

        <label htmlFor="password">{t('login.password')}</label>
        <input
          id="password"
          type="password"
          autoComplete="current-password"
          required
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          disabled={submitting}
        />

        <button type="submit" disabled={submitting}>
          {submitting ? t('login.signingIn') : t('login.signIn')}
        </button>

        {error !== null && (
          <div className="login-error" role="alert">
            {error}
          </div>
        )}
      </form>
    </div>
  );
}
