// What the server offers for "copy subject": the cutout at all, and tapping
// one subject to keep only that one. Asked once per app run and remembered:
// the answer only changes when the operator redeploys. A failed question is
// not remembered, so a later viewer asks again.

import { useEffect, useState } from 'react';
import { getSubjectCutoutAvailability } from '../api/subjectCutout.ts';

export interface SubjectCutoutSupport {
  available: boolean;
  pointSelection: boolean;
}

const NONE: SubjectCutoutSupport = { available: false, pointSelection: false };

let answer: Promise<SubjectCutoutSupport> | null = null;

function askOnce(): Promise<SubjectCutoutSupport> {
  if (answer === null) {
    answer = getSubjectCutoutAvailability().then(
      (r) => ({ available: r.available === true, pointSelection: r.available === true && r.pointSelection === true }),
      () => {
        answer = null;
        return NONE;
      },
    );
  }
  return answer;
}

export function useSubjectCutoutSupport(): SubjectCutoutSupport {
  const [support, setSupport] = useState<SubjectCutoutSupport>(NONE);
  useEffect(() => {
    let live = true;
    void askOnce().then((value) => {
      if (live) setSupport(value);
    });
    return () => {
      live = false;
    };
  }, []);
  return support;
}
