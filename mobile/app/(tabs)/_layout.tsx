// Bottom tabs: Photos / Videos / Albums / Files.
//
// Four browsing destinations, and Sync is deliberately not one of them
// (NUBARCA-UX-01 §5): those are places you look at, while synchronisation is a
// capability you configure once. It lives under Account now.
import React from 'react';
import { Tabs } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import { useI18n } from '../../src/i18n';
import { iconSizes } from '../../src/ui/tokens';
import { BrandTabBar } from '../../src/ui/BrandTabBar';

export default function TabsLayout(): React.JSX.Element {
  const { t } = useI18n();
  return (
    // The bar is ours (BRAND-APP-02 §D); the ROUTING stays React Navigation's.
    //
    // `tabBarStyle.position: 'absolute'` is what stops the navigator reserving
    // a strip below the scene: without it the bar would float AND the scene
    // would still be shortened by its height, leaving a dead band under the
    // gallery (NUBARCA-UX-01 §5).
    <Tabs
      tabBar={(props) => <BrandTabBar {...props} />}
      screenOptions={{
        headerShown: false,
        tabBarStyle: { position: 'absolute' },
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
    </Tabs>
  );
}
