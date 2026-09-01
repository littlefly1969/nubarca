// Sync screen: a simple, explicit control surface for device-media sync.
//
// Business rule of the screen: it renders engine snapshots and forwards
// user intents; ALL decisions (policy, retry, eligibility) live in the
// engine. Enabling asks for media permission HERE — the only place in the
// app that ever prompts — and defaults to new-media-only; historical media
// requires the separate explicit action below.

import React, { useState } from 'react';
import { ActivityIndicator, ScrollView, StyleSheet, Switch, Text, View } from 'react-native';
import { useI18n } from '../i18n';
import type { MobileMessageKey } from '../i18n/it';
import { AppHeader, Screen, SectionTitle } from '../ui/components';
import { deriveUiStatus, type SyncUiStatus } from './syncPolicy.ts';
import { useSync } from './SyncProvider';
import { mediaLibraryPort } from './mediaLibraryAdapter';
import { themed, useColors } from '../ui/theme.ts';

function statusKey(status: SyncUiStatus): MobileMessageKey {
  switch (status) {
    case 'off':
      return 'sync.status.off';
    case 'permission-required':
      return 'sync.status.permissionRequired';
    case 'scanning':
      return 'sync.status.scanning';
    case 'pending':
      return 'sync.status.pending';
    case 'uploading':
      return 'sync.status.uploading';
    case 'paused':
      return 'sync.status.paused';
    case 'waiting-wifi':
      return 'sync.status.waitingWifi';
    case 'up-to-date':
      return 'sync.status.upToDate';
    case 'attention':
      return 'sync.status.attention';
    case 'auth-required':
      return 'sync.status.authRequired';
  }
}

export function SyncScreen(): React.JSX.Element {
  const styles = useStyles();
  const colors = useColors();
  const { t } = useI18n();
  const { engine, snapshot } = useSync();
  const [requestingPermission, setRequestingPermission] = useState(false);

  const enabled = snapshot?.settings.enabled ?? false;
  const wifiOnly = snapshot?.settings.wifiOnly ?? true;
  const includeExisting = snapshot?.settings.includeExisting ?? false;
  const status = snapshot ? deriveUiStatus(snapshot) : 'off';

  const handleEnable = async (): Promise<void> => {
    if (!engine || requestingPermission) return;
    setRequestingPermission(true);
    try {
      // THE permission prompt: only ever from this explicit user gesture.
      const permission = await mediaLibraryPort.requestPermissions();
      if (permission === 'denied' || permission === 'undetermined') {
        return; // stay off; UI shows what is missing
      }
      engine.enable();
    } finally {
      setRequestingPermission(false);
    }
  };

  const formatTime = (ms: number | null): string => {
    if (ms === null) return t('sync.never');
    return new Date(ms).toLocaleString();
  };

  const failedCount =
    (snapshot?.permanentCount ?? 0) + (snapshot?.retryableCount ?? 0);

  return (
    <Screen>
      <AppHeader title={t('tabs.sync')} />
      <ScrollView contentContainerStyle={styles.content}>
        <View style={styles.statusCard}>
          <Text style={styles.statusLabel}>{t('sync.currentStatus')}</Text>
          <Text style={[styles.statusText, status === 'attention' && styles.statusAttention]}>
            {t(statusKey(status))}
          </Text>
          {snapshot ? (
            <Text style={styles.metaLine}>
              {t('sync.lastSync')}: {formatTime(snapshot.lastSyncAt)}
            </Text>
          ) : null}
        </View>

        <View style={styles.row}>
          <Text style={styles.rowLabel}>{t('sync.enableLabel')}</Text>
          <Switch
            value={enabled}
            disabled={!engine || requestingPermission}
            onValueChange={(value) => {
              if (!engine) return;
              if (value) void handleEnable();
              else engine.disable();
            }}
            trackColor={{ false: colors.surfaceMuted, true: colors.accent }}
          />
        </View>
        {requestingPermission ? (
          <View style={styles.hintRow}>
            <ActivityIndicator size="small" color={colors.accent} />
            <Text style={styles.hint}>{t('sync.requestingPermission')}</Text>
          </View>
        ) : null}
        {status === 'permission-required' && !requestingPermission ? (
          <Text style={styles.hint}>{t('sync.permissionHint')}</Text>
        ) : null}
        {status === 'waiting-wifi' ? (
          <Text style={styles.hint}>{t('sync.waitingWifiHint')}</Text>
        ) : null}

        {snapshot ? (
          <View style={styles.counts}>
            <Text style={styles.countLine}>
              {t('sync.pendingCount', { count: snapshot.pendingCount })} ·{' '}
              {t('sync.uploadingCount', { count: snapshot.uploadingCount })} ·{' '}
              {t('sync.completedCount', { count: snapshot.completedCount })}
            </Text>
            {failedCount > 0 ? (
              <Text style={styles.countFailed}>
                {t('sync.failedCount', { count: failedCount })}
              </Text>
            ) : null}
          </View>
        ) : null}

        <SectionTitle text={t('sync.controlsTitle')} />

        <View style={styles.buttonGroup}>
          <Text
            style={[styles.button, (!engine || !enabled) && styles.buttonDisabled]}
            onPress={() => engine?.syncNow()}
          >
            {t('sync.syncNow')}
          </Text>
          <Text
            style={[styles.button, (!engine || !enabled) && styles.buttonDisabled]}
            onPress={() => {
              if (snapshot?.phase === 'paused' || status === 'paused') engine?.resume();
              else engine?.pause();
            }}
          >
            {snapshot?.phase === 'paused' || status === 'paused'
              ? t('sync.resume')
              : t('sync.pause')}
          </Text>
          {failedCount > 0 ? (
            <Text style={[styles.button, styles.retryButton]} onPress={() => engine?.retryFailedItems()}>
              {t('sync.retryFailed')}
            </Text>
          ) : null}
        </View>

        <SectionTitle text={t('sync.settingsTitle')} />

        <View style={styles.row}>
          <Text style={styles.rowLabel}>{t('sync.wifiOnly')}</Text>
          <Switch
            value={wifiOnly}
            disabled={!engine}
            onValueChange={(value) => engine?.updateSettings({ wifiOnly: value })}
            trackColor={{ false: colors.surfaceMuted, true: colors.accent }}
          />
        </View>

        {!includeExisting ? (
          <>
            <Text style={styles.hint}>{t('sync.newMediaOnlyNote')}</Text>
            <Text
              style={[styles.button, (!engine || !enabled) && styles.buttonDisabled]}
              onPress={() => engine?.updateSettings({ includeExisting: true })}
            >
              {t('sync.includeExisting')}
            </Text>
          </>
        ) : (
          <Text style={styles.hint}>{t('sync.includeExistingOn')}</Text>
        )}

        <Text style={styles.privacyNote}>{t('sync.privacyNote')}</Text>
      </ScrollView>
    </Screen>
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    content: {
      padding: 16,
      gap: 12,
    },
    statusCard: {
      backgroundColor: colors.surface,
      borderRadius: 14,
      padding: 16,
      gap: 4,
    },
    statusLabel: {
      fontSize: 12,
      color: colors.textSecondary,
      textTransform: 'uppercase',
    },
    statusText: {
      fontSize: 20,
      fontWeight: '600',
      color: colors.textPrimary,
    },
    statusAttention: {
      color: colors.danger,
    },
    metaLine: {
      fontSize: 12,
      color: colors.textTertiary,
    },
    row: {
      flexDirection: 'row',
      alignItems: 'center',
      justifyContent: 'space-between',
    },
    rowLabel: {
      fontSize: 15,
      color: colors.textPrimary,
    },
    counts: {
      gap: 2,
    },
    countLine: {
      fontSize: 13,
      color: colors.textSecondary,
    },
    countFailed: {
      fontSize: 13,
      color: colors.danger,
    },
    buttonGroup: {
      gap: 8,
    },
    button: {
      backgroundColor: colors.surface,
      borderColor: colors.separator,
      borderWidth: 1,
      borderRadius: 10,
      paddingHorizontal: 14,
      paddingVertical: 10,
      fontSize: 15,
      color: colors.textPrimary,
      overflow: 'hidden',
      textAlign: 'center',
    },
    buttonDisabled: {
      opacity: 0.4,
    },
    retryButton: {
      borderColor: colors.danger,
    },
    hint: {
      fontSize: 13,
      color: colors.textSecondary,
    },
    hintRow: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: 8,
    },
    privacyNote: {
      fontSize: 12,
      color: colors.textTertiary,
      marginTop: 8,
    },
  }),
);
