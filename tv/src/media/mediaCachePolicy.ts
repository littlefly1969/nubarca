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

// Pending-task ownership is deliberately separate from file leases: a
// subscriber still needs network work, while a lease protects resolved bytes.
export function createMediaSubscribers() {
  let count = 0;
  return {
    acquire: () => {
      count += 1;
      let active = true;
      return () => {
        if (!active) return;
        active = false;
        count -= 1;
      };
    },
    hasAny: () => count > 0,
  };
}

export interface MediaWaiter {
  canStart: () => boolean;
  start: () => void;
  discard: () => void;
}

function startAt(queue: MediaWaiter[], index: number): void {
  queue[index].start();
  for (let i = 0; i < index; i += 1) queue[i].discard();
  queue.splice(0, index + 1);
}

// Hand a freed slot to the first live high/low waiter. Stale waiters are all
// discarded synchronously, so one current tile never sits behind hundreds of
// promises left by a long D-pad repeat.
export function handoffFirstLive(
  high: MediaWaiter[],
  low: MediaWaiter[],
): boolean {
  const highIndex = high.findIndex((waiter) => waiter.canStart());
  if (highIndex >= 0) {
    startAt(high, highIndex);
    return true;
  }
  const lowIndex = low.findIndex((waiter) => waiter.canStart());
  if (lowIndex >= 0) {
    startAt(low, lowIndex);
    high.forEach((waiter) => waiter.discard());
    high.length = 0;
    return true;
  }
  high.forEach((waiter) => waiter.discard());
  low.forEach((waiter) => waiter.discard());
  high.length = 0;
  low.length = 0;
  return false;
}
