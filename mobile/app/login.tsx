// Login screen. Adapts the proven skeleton form: editable server URL
// (prefilled with the last server), email, password, one accent action.
// A wrong password is a 401 handled HERE — it must not tear down any session.

import React, { useEffect, useState } from 'react';
import {
  ActivityIndicator,
  KeyboardAvoidingView,
  Platform,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { Redirect } from 'expo-router';
import Constants from 'expo-constants';
import { Screen } from '../src/ui/components';
import { useSession } from '../src/session/SessionProvider';
import { getStoredBaseUrl } from '../src/api/session';
import { configureBaseUrl, ApiError } from '../src/api/client';
import { useI18n } from '../src/i18n';
import { radii, spacing, touch } from '../src/ui/tokens';
import { themed, useColors } from '../src/ui/theme.ts';

// Android emulator reaches the host at 10.0.2.2; iOS simulator uses localhost.
const configuredUrl = (
  Constants.expoConfig?.extra as { apiBaseUrl?: string } | undefined
)?.apiBaseUrl;
const defaultBaseUrl =
  configuredUrl ??
  (Platform.OS === 'android' ? 'http://10.0.2.2:5177' : 'http://localhost:5177');

export default function Login(): React.JSX.Element {
  const styles = useStyles();
  const colors = useColors();
  const session = useSession();
  const { t } = useI18n();
  const [baseUrl, setBaseUrl] = useState(defaultBaseUrl);
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Prefill the last server used — not a secret, kept across sign-outs.
  useEffect(() => {
    let cancelled = false;
    void getStoredBaseUrl().then((stored) => {
      if (!cancelled && stored !== null && stored.length > 0) setBaseUrl(stored);
    });
    return () => {
      cancelled = true;
    };
  }, []);

  async function submit(): Promise<void> {
    setBusy(true);
    setError(null);
    try {
      configureBaseUrl(baseUrl.trim());
      await session.login(baseUrl.trim(), email.trim(), password);
      // Navigation happens via the authed redirect in this screen.
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        setError(t('login.errorCredentials'));
      } else {
        setError(t('login.errorNetwork'));
      }
    } finally {
      setBusy(false);
    }
  }

  // Already signed in (e.g. navigated back)? Leave.
  if (session.status === 'authed') {
    return <Redirect href="/(tabs)/photos" />;
  }

  return (
    <Screen>
      <KeyboardAvoidingView
        style={styles.flex}
        behavior={Platform.OS === 'ios' ? 'padding' : undefined}
      >
        <ScrollView contentContainerStyle={styles.container} keyboardShouldPersistTaps="handled">
          <Text style={styles.title}>NubArca</Text>

          {session.expired && (
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
            accessibilityLabel={t('login.apiBaseUrl')}
          />
          <Text style={styles.hint}>{t('login.serverHint')}</Text>

          <Text style={styles.label}>{t('login.email')}</Text>
          <TextInput
            style={styles.input}
            value={email}
            onChangeText={setEmail}
            autoCapitalize="none"
            keyboardType="email-address"
            textContentType="emailAddress"
            editable={!busy}
            accessibilityLabel={t('login.email')}
          />

          <Text style={styles.label}>{t('login.password')}</Text>
          <TextInput
            style={styles.input}
            value={password}
            onChangeText={setPassword}
            secureTextEntry
            textContentType="password"
            editable={!busy}
            accessibilityLabel={t('login.password')}
          />

          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('login.signIn')}
            style={({ pressed }) => [
              styles.button,
              pressed && styles.pressed,
              busy && styles.buttonDisabled,
            ]}
            onPress={() => {
              void submit();
            }}
            disabled={busy}
          >
            {busy ? (
              <ActivityIndicator color={colors.textOnAccent} />
            ) : (
              <Text style={styles.buttonText}>{t('login.signIn')}</Text>
            )}
          </Pressable>

          {error !== null && (
            <View style={styles.errorCard}>
              <Text style={styles.errorText}>{error}</Text>
            </View>
          )}
        </ScrollView>
      </KeyboardAvoidingView>
    </Screen>
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    flex: { flex: 1 },
    container: {
      flexGrow: 1,
      padding: spacing.xl,
      paddingTop: spacing.xxl + spacing.l,
    },
    title: {
      fontSize: 26,
      fontWeight: '700',
      marginBottom: spacing.xl,
      color: colors.textPrimary,
    },
    notice: {
      padding: spacing.m,
      backgroundColor: colors.warningSurface,
      borderRadius: radii.m,
      marginBottom: spacing.m,
    },
    noticeText: { color: colors.warningText, fontSize: 13 },
    label: {
      fontSize: 12,
      fontWeight: '600',
      color: colors.textSecondary,
      marginTop: spacing.m,
      marginBottom: spacing.xs,
      textTransform: 'uppercase',
      letterSpacing: 0.5,
    },
    hint: {
      fontSize: 12,
      color: colors.textTertiary,
      marginTop: spacing.xs,
    },
    input: {
      borderWidth: 1,
      borderColor: colors.separator,
      borderRadius: radii.m,
      paddingHorizontal: spacing.m,
      paddingVertical: spacing.m - 2,
      backgroundColor: colors.surface,
      fontSize: 15,
      color: colors.textPrimary,
    },
    button: {
      marginTop: spacing.xl,
      backgroundColor: colors.accentStrong,
      borderRadius: radii.m,
      minHeight: touch.minSize,
      alignItems: 'center',
      justifyContent: 'center',
    },
    buttonDisabled: { backgroundColor: colors.accentDisabled },
    pressed: { opacity: 0.8 },
    buttonText: { color: colors.textOnAccent, fontWeight: '600', fontSize: 15 },
    errorCard: {
      marginTop: spacing.l,
      padding: spacing.m,
      backgroundColor: colors.dangerSurface,
      borderRadius: radii.m,
    },
    errorText: { color: colors.danger, fontSize: 13 },
  }),
);

