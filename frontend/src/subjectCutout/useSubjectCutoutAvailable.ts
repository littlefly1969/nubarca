import { useEffect, useState } from 'react';
import { getSubjectCutoutAvailability } from '@nubarca/api-client';

// What this installation offers for "copy subject": the cutout at all, and
// tapping one subject to keep only that one. Asked once per page load and
// shared by every viewer; a failed request is not remembered, so the next
// viewer that opens asks again.
export interface SubjectCutoutSupport {
  available: boolean;
  pointSelection: boolean;
}

const NONE: SubjectCutoutSupport = { available: false, pointSelection: false };

let pending: Promise<SubjectCutoutSupport> | null = null;

function loadSupport(): Promise<SubjectCutoutSupport> {
  if (pending === null) {
    pending = getSubjectCutoutAvailability()
      .then((r) => ({ available: r.available === true, pointSelection: r.available === true && r.pointSelection === true }))
      .catch(() => {
        pending = null;
        return NONE;
      });
  }
  return pending;
}

export function useSubjectCutoutSupport(enabled: boolean): SubjectCutoutSupport {
  const [support, setSupport] = useState<SubjectCutoutSupport>(NONE);
  useEffect(() => {
    if (!enabled) return;
    let live = true;
    void loadSupport().then((value) => { if (live) setSupport(value); });
    return () => { live = false; };
  }, [enabled]);
  return enabled ? support : NONE;
}

export function useSubjectCutoutAvailable(enabled: boolean): boolean {
  return useSubjectCutoutSupport(enabled).available;
}

export function resetSubjectCutoutAvailabilityForTests(): void {
  pending = null;
}
