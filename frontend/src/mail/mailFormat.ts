import type { MailAddress } from './types';

// "Ada Example <ada@example.test>", or whichever half exists. Nothing is
// invented: an address with neither part renders as an empty string and the
// caller decides what to show instead.
export function formatAddress(a: MailAddress | null | undefined): string {
  if (!a) return '';
  const name = a.name?.trim() ?? '';
  const address = a.address?.trim() ?? '';
  if (name && address && name !== address) return `${name} <${address}>`;
  return name || address;
}

export function formatAddressList(list: readonly MailAddress[]): string {
  return list.map(formatAddress).filter((s) => s.length > 0).join(', ');
}

// The short form for a list row: the display name when there is one.
export function senderLabel(a: MailAddress | null | undefined): string {
  if (!a) return '';
  return a.name?.trim() || a.address?.trim() || '';
}
