// The Account affordance, in one place.
//
// It belongs on every primary surface (NUBARCA-UX-01.1 §6), which is exactly
// the situation where five screens grow five slightly different
// `person-circle-outline` buttons: different sizes, different labels, one of
// them eventually pointing somewhere else. One component means one icon, one
// size, one accessible name and one destination.
//
// Account is GLOBAL. Contextual actions — filter, create, contribute — sit
// beside it and stay local to the surface that owns them.

import React from 'react';
import { Ionicons } from '@expo/vector-icons';
import { router } from 'expo-router';
import { IconButton } from './components';
import { iconSizes } from './tokens';
import { useColors } from './theme';
import { useI18n } from '../i18n';

export function AccountButton(): React.JSX.Element {
  const colors = useColors();
  const { t } = useI18n();
  return (
    <IconButton
      accessibilityLabel={t('account.open')}
      onPress={() => router.push('/account')}
    >
      <Ionicons name="person-circle-outline" size={iconSizes.l} color={colors.accent} />
    </IconButton>
  );
}
