// Synchronisation, reached from the Account hub (NUBARCA-UX-01 §11).
//
// It used to be a primary destination in the tab bar, beside Photos, Videos and
// Albums. It is not one: those are places you browse, and this is a capability
// you configure once and then forget. Sitting in the bar it took a fifth of the
// navigation for something most people open twice.
//
// The screen and its engine are untouched — only where you find it changed.
import React from 'react';
import { SyncScreen } from '../src/sync/SyncScreen';

export default function SyncPage(): React.JSX.Element {
  return <SyncScreen />;
}
