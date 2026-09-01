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
import { GestureHandlerRootView } from 'react-native-gesture-handler';
import { I18nProvider, useI18n } from '../src/i18n';
import { SessionProvider, useSession } from '../src/session/SessionProvider';
import { ViewerProvider } from '../src/media/viewerContext';
import { viewerIdentityKey } from '../src/media/viewerIdentity';
import { SyncProvider } from '../src/sync/SyncProvider';
import { ThemeProvider, themed, useColors, useTheme } from '../src/ui/theme';

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

  if (session.status === 'unauthed') {
    return (
      <>
        {/* A fresh install must reach login without opening authenticated
            storage or starting background services. */}
        <Redirect href="/login" />
        <AppStack identityKey="signed-out" />
      </>
    );
  }

  // The public context shape deliberately exposes user as nullable. Keep this
  // boundary defensive even though SessionProvider emits a user for `authed`.
  if (session.user === null) {
    return (
      <>
        <Redirect href="/login" />
        <AppStack identityKey="missing-session-user" />
      </>
    );
  }

  const userId = session.user.id;
  return (
    // Sync owns authenticated, per-account durable state. Never construct it
    // for the signed-out pseudo-identity: a storage fault must not gate login.
    <SyncProvider key={userId} accountId={userId}>
      <AppStack identityKey={userId} />
    </SyncProvider>
  );
}

function AppStack({ identityKey }: { identityKey: string }): React.JSX.Element {
  const colors = useColors();
  return (
    <Stack
      key={identityKey}
      screenOptions={{
        headerShown: false,
        animation: 'slide_from_right',
        // The surface a screen slides ACROSS belongs to the native stack, not
        // to any screen, so nothing else can paint it: without this a push
        // reveals a white gutter for the length of the animation.
        contentStyle: { backgroundColor: colors.canvas },
      }}
    >
      <Stack.Screen name="(tabs)" />
      <Stack.Screen name="login" options={{ animation: 'fade' }} />
      <Stack.Screen name="album/[id]" />
      <Stack.Screen name="settings" />
      <Stack.Screen
        name="media/[id]"
        options={{ presentation: 'fullScreenModal', animation: 'fade' }}
      />
    </Stack>
  );
}

function Splash(): React.JSX.Element {
  const styles = useStyles();
  const colors = useColors();
  const { t } = useI18n();
  return (
    <View style={styles.splash}>
      <ActivityIndicator size="large" color={colors.accent} />
      <Text style={styles.splashText}>{t('app.restoring')}</Text>
    </View>
  );
}

// The status bar's CONTENT — the clock, the battery, the signal — is drawn by
// the system, so it has to contrast with OUR canvas, not with the phone's own
// theme. A user on a light phone who chooses the dark theme would otherwise get
// dark glyphs on Midnight Navy: an invisible status bar.
function ThemedStatusBar(): React.JSX.Element {
  const { theme } = useTheme();
  return <StatusBar style={theme === 'dark' ? 'light' : 'dark'} />;
}

export default function RootLayout(): React.JSX.Element {
  // GestureHandlerRootView must be the OUTERMOST view: react-native-gesture-
  // handler resolves every gesture against the nearest root, and without one
  // the viewer's pinch/pan simply never fire on Android — silently, with no
  // error to point at. It carries no colour of its own, so it is the one
  // stylesheet in the app that is NOT themed — it sits outside the provider
  // that would supply the palette.
  return (
    <GestureHandlerRootView style={rootStyles.gestureRoot}>
      <ThemeProvider>
        <SafeAreaProvider>
          <I18nProvider>
            <SessionProvider>
              <IdentityKeyedViewerProvider>
                <ThemedStatusBar />
                <RootGate />
              </IdentityKeyedViewerProvider>
            </SessionProvider>
          </I18nProvider>
        </SafeAreaProvider>
      </ThemeProvider>
    </GestureHandlerRootView>
  );
}

const rootStyles = StyleSheet.create({ gestureRoot: { flex: 1 } });

const useStyles = themed((colors) =>
  StyleSheet.create({
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
  }),
);
