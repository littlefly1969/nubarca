import * as Updates from 'expo-updates';
import Constants from 'expo-constants';
import { startBackgroundUpdateCheck as startCheck } from './updateLifecycle';

export function startBackgroundUpdateCheck(): void {
  const extra = (Constants.expoConfig?.extra ?? {}) as { otaChannel?: string };
  void startCheck(Updates, {
    applicationVersion: Constants.expoConfig?.version ?? null,
    versionCode: Constants.expoConfig?.android?.versionCode ?? null,
    channel: extra.otaChannel ?? null,
  });
}
