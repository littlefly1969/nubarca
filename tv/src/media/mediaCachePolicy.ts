export function oldestEvictableIndex(
  order: readonly string[],
  leases: ReadonlyMap<string, number>,
  protectedKey?: string,
): number {
  return order.findIndex((key) => key !== protectedKey && (leases.get(key) ?? 0) === 0);
}

export function isSoleLease(leases: ReadonlyMap<string, number>, key: string): boolean {
  return leases.get(key) === 1;
}
