import { useEffect, useState } from 'react';
import { getSubjectCutoutAvailability } from '@nubarca/api-client';

// Whether this installation offers "copy subject". Asked once per page load
// and shared by every viewer; a failed request is not remembered, so the next
// viewer that opens asks again.
let pending: Promise<boolean> | null = null;

function loadAvailability(): Promise<boolean> {
  if (pending === null) {
    pending = getSubjectCutoutAvailability()
      .then((r) => r.available === true)
      .catch(() => {
        pending = null;
        return false;
      });
  }
  return pending;
}

export function useSubjectCutoutAvailable(enabled: boolean): boolean {
  const [available, setAvailable] = useState(false);
  useEffect(() => {
    if (!enabled) return;
    let live = true;
    void loadAvailability().then((value) => { if (live) setAvailable(value); });
    return () => { live = false; };
  }, [enabled]);
  return enabled && available;
}

export function resetSubjectCutoutAvailabilityForTests(): void {
  pending = null;
}
