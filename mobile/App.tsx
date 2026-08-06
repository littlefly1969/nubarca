import React, { useCallback, useEffect, useState } from 'react';
import { ActivityIndicator, StyleSheet, Text, View } from 'react-native';
import { StatusBar } from 'expo-status-bar';
import LoginScreen from './src/screens/LoginScreen';
import GalleryScreen from './src/screens/GalleryScreen';
import { restoreSession, clearSession, ApiError } from './src/api/client';
import { fetchCurrentUser } from './src/api/auth';
import type { CurrentUser } from './src/api/auth';
import { I18nProvider, useI18n, toLanguage } from './src/i18n';

// Top-level router + session bootstrap. Simple state, no navigation library.
//
//   restoring  → trying to restore a persisted cookie and validate it
//   unauthed   → show login (optionally with an "expired" notice)
//   authed     → show the read-only gallery
type AppState =
  | { phase: 'restoring' }
  | { phase: 'unauthed'; expired: boolean }
  | { phase: 'authed'; user: CurrentUser };

// Root: provides i18n to the whole app. The active language is the signed-in
// user's persisted preference (adopted once the session resolves); Italian is
// the default until then.
export default function App(): React.JSX.Element {
  return (
    <I18nProvider>
      <AppInner />
    </I18nProvider>
  );
}

function AppInner(): React.JSX.Element {
  const { t, setLanguage } = useI18n();
  const [state, setState] = useState<AppState>({ phase: 'restoring' });

  // Adopt the authenticated user's persisted UI language.
  const adoptUserLanguage = useCallback((user: CurrentUser) => {
    const lang = toLanguage(user.language);
    if (lang) setLanguage(lang);
  }, [setLanguage]);

  // Stable so GalleryScreen can depend on it without re-running effects. A
  // mid-session 401 passes { expired: true } to surface the login notice.
  const handleSignedOut = useCallback((opts?: { expired?: boolean }) => {
    setState({ phase: 'unauthed', expired: opts?.expired ?? false });
  }, []);

  // On cold start: restore a persisted cookie and validate it against
  // /api/auth/me. A 401 means the saved cookie expired — clear it and show the
  // login with a notice. No persisted cookie → straight to login.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const restored = await restoreSession();
        if (!restored) {
          if (!cancelled) setState({ phase: 'unauthed', expired: false });
          return;
        }
        const user = await fetchCurrentUser();
        if (!cancelled) {
          adoptUserLanguage(user);
          setState({ phase: 'authed', user });
        }
      } catch (err) {
        // Expired/invalid cookie (401) or any validation failure: drop it.
        await clearSession();
        const expired = err instanceof ApiError && err.status === 401;
        if (!cancelled) setState({ phase: 'unauthed', expired });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [adoptUserLanguage]);

  return (
    <>
      <StatusBar style="auto" />
      {state.phase === 'restoring' ? (
        <View style={styles.center}>
          <ActivityIndicator size="large" color="#1a73e8" />
          <Text style={styles.restoringText}>{t('app.restoring')}</Text>
        </View>
      ) : state.phase === 'authed' ? (
        <GalleryScreen onLogout={handleSignedOut} />
      ) : (
        <LoginScreen
          sessionExpired={state.expired}
          onAuth={(user) => {
            adoptUserLanguage(user);
            setState({ phase: 'authed', user });
          }}
        />
      )}
    </>
  );
}

const styles = StyleSheet.create({
  center: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: '#f5f5f5',
  },
  restoringText: { marginTop: 16, color: '#555', fontSize: 14 },
});
