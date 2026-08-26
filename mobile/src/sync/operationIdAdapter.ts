// Runtime OPERATION-identity generator (mobile-sync-v1 hardening).
//
// 16 bytes from Expo's CSPRNG (platform SecureRandom via expo-crypto),
// formatted as 32 hex chars by the pure formatter in syncPolicy. The value is
// generated ONCE per logical ledger row, persisted there, and reused across
// every retry/restart — it is an operation identity, never a content hash,
// and carries no readable account/asset/information.

import * as Crypto from 'expo-crypto';
import { formatOperationId } from './syncPolicy.ts';

export function newOperationId(): string {
  return formatOperationId(Crypto.getRandomBytes(16));
}
