import React, { useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Platform,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import Constants from 'expo-constants';
import {
  configure,
  clearCookies,
  cookieStatus,
  persistSession,
  getStoredBaseUrl,
  ApiError,
} from '../api/client';
import { loginRequest, fetchCurrentUser } from '../api/auth';
import type { CurrentUser } from '../api/auth';
import { useI18n } from '../i18n';

// Android emulator reaches the host machine at 10.0.2.2; iOS simulator uses
// localhost. Override via app.json > extra.apiBaseUrl for device/prod testing.
const configuredUrl = (
  Constants.expoConfig?.extra as { apiBaseUrl?: string } | undefined
)?.apiBaseUrl;
const defaultBaseUrl =
  configuredUrl ??
  (Platform.OS === 'android'
    ? 'http://10.0.2.2:5177'
    : 'http://localhost:5177');

type Status =
  | { phase: 'idle' }
  | { phase: 'loading' }
  | { phase: 'error'; message: string };

// Login + cookie-jar setup. On success it hands the authenticated user and the
// resolved base URL up to App, which switches to the gallery. The cookie jar
// (in client.ts) is configured here and reused by every later request.
export default function LoginScreen({
  onAuth,
  sessionExpired = false,
}: {
  onAuth: (user: CurrentUser) => void;
  sessionExpired?: boolean;
}): React.JSX.Element {
  const { t } = useI18n();
  const [baseUrl, setBaseUrl] = useState(defaultBaseUrl);
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [status, setStatus] = useState<Status>({ phase: 'idle' });

  // Prefill the last server used (stored alongside the session, carries no
  // secret) so a re-login does not require retyping the URL.
  useEffect(() => {
    let cancelled = false;
    void getStoredBaseUrl().then((stored) => {
      if (!cancelled && stored !== null && stored.length > 0) setBaseUrl(stored);
    });
    return () => {
      cancelled = true;
    };
  }, []);

  async function login(): Promise<void> {
    setStatus({ phase: 'loading' });
    clearCookies();
    configure(baseUrl);
    let step: 'login' | 'me' = 'login';
    try {
      await loginRequest(email, password);
      step = 'me';
      const me = await fetchCurrentUser();
      // Persist the validated session so it survives an app restart.
      await persistSession();
      onAuth(me);
    } catch (err) {
      const where =
        step === 'login'
          ? 'POST /api/auth/login (credentials rejected?)'
          : 'GET /api/auth/me (login OK but cookie not forwarded?)';
      const { captured, preview } = cookieStatus();
      const cookieLine = `\n\nCookie after login: ${
        captured ? `captured (${preview})` : 'NOT captured'
      }`;
      const detail =
        err instanceof ApiError
          ? `ApiError ${err.status}\n${JSON.stringify(err.body, null, 2)}`
          : String(err);
      setStatus({
        phase: 'error',
        message: `Failed at: ${where}\n\n${detail}${cookieLine}`,
      });
    }
  }

  const busy = status.phase === 'loading';

  return (
    <ScrollView contentContainerStyle={styles.container}>
      <Text style={styles.title}>NubArca</Text>

      {sessionExpired && (
        <View style={styles.notice}>
          <Text style={styles.noticeText}>{t('login.sessionExpired')}</Text>
        </View>
      )}

      <Text style={styles.label}>{t('login.apiBaseUrl')}</Text>
      <TextInput
        style={styles.input}
        value={baseUrl}
        onChangeText={setBaseUrl}
        autoCapitalize="none"
        autoCorrect={false}
        keyboardType="url"
        editable={!busy}
      />

      <Text style={styles.label}>{t('login.email')}</Text>
      <TextInput
        style={styles.input}
        value={email}
        onChangeText={setEmail}
        autoCapitalize="none"
        keyboardType="email-address"
        textContentType="emailAddress"
        editable={!busy}
      />

      <Text style={styles.label}>{t('login.password')}</Text>
      <TextInput
        style={styles.input}
        value={password}
        onChangeText={setPassword}
        secureTextEntry
        textContentType="password"
        editable={!busy}
      />

      <Pressable
        style={[styles.button, busy && styles.buttonDisabled]}
        onPress={() => {
          void login();
        }}
        disabled={busy}
      >
        {busy ? (
          <ActivityIndicator color="#fff" />
        ) : (
          <Text style={styles.buttonText}>{t('login.signIn')}</Text>
        )}
      </Pressable>

      {status.phase === 'error' && (
        <View style={styles.errorCard}>
          <Text style={styles.errorText}>{status.message}</Text>
        </View>
      )}
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  container: {
    flexGrow: 1,
    padding: 20,
    paddingTop: 80,
    backgroundColor: '#f5f5f5',
  },
  title: { fontSize: 24, fontWeight: '700', marginBottom: 24, color: '#111' },
  notice: {
    padding: 12,
    backgroundColor: '#fff6e0',
    borderRadius: 8,
    borderWidth: 1,
    borderColor: '#f0d088',
    marginBottom: 8,
  },
  noticeText: { color: '#7a5b00', fontSize: 13 },
  label: {
    fontSize: 12,
    fontWeight: '600',
    color: '#555',
    marginTop: 12,
    marginBottom: 4,
    textTransform: 'uppercase',
    letterSpacing: 0.5,
  },
  input: {
    borderWidth: 1,
    borderColor: '#ccc',
    borderRadius: 8,
    paddingHorizontal: 12,
    paddingVertical: 10,
    backgroundColor: '#fff',
    fontSize: 15,
  },
  button: {
    marginTop: 24,
    backgroundColor: '#1a73e8',
    borderRadius: 8,
    paddingVertical: 14,
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: 48,
  },
  buttonDisabled: { backgroundColor: '#a0b9e4' },
  buttonText: { color: '#fff', fontWeight: '600', fontSize: 15 },
  errorCard: {
    marginTop: 16,
    padding: 12,
    backgroundColor: '#fff8f8',
    borderRadius: 8,
    borderWidth: 1,
    borderColor: '#f5a0a0',
  },
  errorText: {
    fontFamily: Platform.OS === 'ios' ? 'Menlo' : 'monospace',
    fontSize: 12,
    color: '#a33',
  },
});
