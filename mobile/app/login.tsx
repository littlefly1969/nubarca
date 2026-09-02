// Login screen. The one full-bleed moment the app has before the library, and
// the first place the product should be recognisable.
//
// BEHAVIOUR IS UNCHANGED by the BRAND-APP-02 redesign: the server URL is still
// editable and still prefilled from the last server used, the same two calls
// run in the same order, a 401 is still classified here as bad credentials and
// everything else as a network problem, and an already-authenticated visitor
// is still redirected to Photos. A wrong password must not tear down a session.

import React, { useEffect, useState } from 'react';
import { KeyboardAvoidingView, Platform, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Redirect } from 'expo-router';
import Constants from 'expo-constants';
import { Button, Screen } from '../src/ui/components';
import { BrandLockup } from '../src/ui/BrandLockup';
import { InlineNotice, TextField } from '../src/ui/fields';
import { useSession } from '../src/session/SessionProvider';
import { getStoredBaseUrl } from '../src/api/session';
import { configureBaseUrl, ApiError } from '../src/api/client';
import { useI18n } from '../src/i18n';
import { spacing, typography } from '../src/ui/tokens';
import { themed } from '../src/ui/theme';

// Android emulator reaches the host at 10.0.2.2; iOS simulator uses localhost.
const configuredUrl = (
  Constants.expoConfig?.extra as { apiBaseUrl?: string } | undefined
)?.apiBaseUrl;
const defaultBaseUrl =
  configuredUrl ??
  (Platform.OS === 'android' ? 'http://10.0.2.2:5177' : 'http://localhost:5177');

const releaseVersion = (
  Constants.expoConfig?.extra as { releaseVersion?: string } | undefined
)?.releaseVersion;

export default function Login(): React.JSX.Element {
  const styles = useStyles();
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
          {/* The approved artwork, in the variant made for the active surface —
              never the product name typed in the heading face. */}
          <BrandLockup visibleWidth={200} style={styles.lockup} />

          <Text style={styles.title}>{t('login.title')}</Text>
          <Text style={styles.subtitle}>{t('login.subtitle')}</Text>

          {session.expired && (
            <View style={styles.notice}>
              <InlineNotice tone="warning" text={t('login.sessionExpired')} />
            </View>
          )}

          <View style={styles.fields}>
            <TextField
              label={t('login.apiBaseUrl')}
              hint={t('login.serverHint')}
              value={baseUrl}
              onChangeText={setBaseUrl}
              autoCapitalize="none"
              autoCorrect={false}
              keyboardType="url"
              editable={!busy}
            />
            <TextField
              label={t('login.email')}
              value={email}
              onChangeText={setEmail}
              autoCapitalize="none"
              keyboardType="email-address"
              textContentType="emailAddress"
              editable={!busy}
            />
            <TextField
              label={t('login.password')}
              value={password}
              onChangeText={setPassword}
              secureTextEntry
              textContentType="password"
              editable={!busy}
            />
          </View>

          <View style={styles.action}>
            <Button
              label={t('login.signIn')}
              loading={busy}
              onPress={() => {
                void submit();
              }}
            />
          </View>

          {error !== null && (
            <View style={styles.notice}>
              <InlineNotice tone="danger" text={error} />
            </View>
          )}

          {releaseVersion !== undefined && (
            <Text style={styles.version}>{`v${releaseVersion}`}</Text>
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
    lockup: { alignItems: 'flex-start', marginBottom: spacing.xl },
    title: { ...typography.pageTitle, color: colors.textPrimary },
    subtitle: {
      ...typography.secondary,
      color: colors.textSecondary,
      marginTop: spacing.xs,
    },
    fields: { marginTop: spacing.xl, gap: spacing.l },
    notice: { marginTop: spacing.l },
    action: { marginTop: spacing.xl },
    version: {
      ...typography.badge,
      color: colors.textTertiary,
      marginTop: spacing.xl,
      textAlign: 'center',
    },
  }),
);
