// Bottom tabs: Photos / Videos / Albums / Files / Sync.
import React from 'react';
import { Tabs } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import { useI18n } from '../../src/i18n';
import { colors, iconSizes } from '../../src/ui/tokens';

export default function TabsLayout(): React.JSX.Element {
  const { t } = useI18n();
  return (
    <Tabs
      screenOptions={{
        headerShown: false,
        tabBarActiveTintColor: colors.accent,
        tabBarInactiveTintColor: colors.textTertiary,
        tabBarLabelStyle: { fontSize: 11 },
      }}
    >
      <Tabs.Screen
        name="photos"
        options={{
          title: t('tabs.photos'),
          tabBarIcon: ({ color, size }) => (
            <Ionicons name="images-outline" size={Math.min(size, iconSizes.l)} color={color} />
          ),
        }}
      />
      <Tabs.Screen
        name="videos"
        options={{
          title: t('tabs.videos'),
          tabBarIcon: ({ color, size }) => (
            <Ionicons name="film-outline" size={Math.min(size, iconSizes.l)} color={color} />
          ),
        }}
      />
      <Tabs.Screen
        name="albums"
        options={{
          title: t('tabs.albums'),
          tabBarIcon: ({ color, size }) => (
            <Ionicons name="albums-outline" size={Math.min(size, iconSizes.l)} color={color} />
          ),
        }}
      />
      <Tabs.Screen
        name="files"
        options={{
          title: t('tabs.files'),
          tabBarIcon: ({ color, size }) => (
            <Ionicons name="folder-open-outline" size={Math.min(size, iconSizes.l)} color={color} />
          ),
        }}
      />
      <Tabs.Screen
        name="sync"
        options={{
          title: t('tabs.sync'),
          tabBarIcon: ({ color, size }) => (
            <Ionicons name="sync-outline" size={Math.min(size, iconSizes.l)} color={color} />
          ),
        }}
      />
    </Tabs>
  );
}
