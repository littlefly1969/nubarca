// Root layout: providers + native stack.
//
// Auth gating rule: while the session is RESTORING, nothing but the splash is
// rendered, so an authenticated route can never flash its content during cold
// start. Signed-out users are redirected to /login; signed-in users are keyed
// by user id so switching accounts remounts every authenticated screen and no
// selection or cached state crosses accounts.

import React from 'react';
import { ActivityIndicator, StyleSheet, Text, View } from 'react-native';
import { Stack, Redirect } from 'expo-router';
import { StatusBar } from 'expo-status-bar';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { I18nProvider, useI18n } from '../src/i18n';
import { SessionProvider, useSession } from '../src/session/SessionProvider';
import { ViewerProvider } from '../src/media/viewerContext';
import { viewerIdentityKey } from '../src/media/viewerIdentity';
import { colors } from '../src/ui/tokens';

function IdentityKeyedViewerProvider({
  children,
}: {
  children: React.ReactNode;
}): React.JSX.Element {
  const session = useSession();
  // PRIVACY: the whole viewer subtree is REMOUNTED whenever the authenticated
  // identity changes — account switch or sign-out. Keying (instead of wiping
  // in an after-render effect) guarantees the FIRST render under the new
  // identity already observes an empty sequence; nothing belonging to the
  // previous account is ever committed under the new one.
  return (
    <ViewerProvider key={viewerIdentityKey(session)}>{children}</ViewerProvider>
  );
}

function RootGate(): React.JSX.Element {
  const session = useSession();

  if (session.status === 'restoring') {
    return <Splash />;
  }

  return (
    <>
      {session.status === 'unauthed' ? (
        // Redirect away from any deep link into authenticated routes.
        <Redirect href="/login" />
      ) : null}
      <Stack
        key={session.user?.id ?? 'anon'}
        screenOptions={{
          headerShown: false,
          animation: 'slide_from_right',
        }}
      >
        <Stack.Screen name="(tabs)" />
        <Stack.Screen name="login" options={{ animation: 'fade' }} />
        <Stack.Screen name="album/[id]" />
        <Stack.Screen name="media/[id]" options={{ presentation: 'fullScreenModal', animation: 'fade' }} />
      </Stack>
    </>
  );
}

function Splash(): React.JSX.Element {
  const { t } = useI18n();
  return (
    <View style={styles.splash}>
      <ActivityIndicator size="large" color={colors.accent} />
      <Text style={styles.splashText}>{t('app.restoring')}</Text>
    </View>
  );
}

export default function RootLayout(): React.JSX.Element {
  return (
    <SafeAreaProvider>
      <I18nProvider>
        <SessionProvider>
          <IdentityKeyedViewerProvider>
            <StatusBar style="dark" />
            <RootGate />
          </IdentityKeyedViewerProvider>
        </SessionProvider>
      </I18nProvider>
    </SafeAreaProvider>
  );
}

const styles = StyleSheet.create({
  splash: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: colors.canvas,
  },
  splashText: {
    marginTop: 16,
    color: colors.textSecondary,
    fontSize: 14,
  },
});
