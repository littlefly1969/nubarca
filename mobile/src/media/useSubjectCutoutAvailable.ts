// Whether the server offers "copy subject" at all. Asked once per app run and
// remembered: the answer only changes when the operator redeploys. A failed
// question is not remembered, so a later viewer asks again.

import { useEffect, useState } from 'react';
import { getSubjectCutoutAvailability } from '../api/subjectCutout.ts';

let answer: Promise<boolean> | null = null;

function askOnce(): Promise<boolean> {
  if (answer === null) {
    answer = getSubjectCutoutAvailability().then(
      (r) => r.available === true,
      () => {
        answer = null;
        return false;
      },
    );
  }
  return answer;
}

export function useSubjectCutoutAvailable(): boolean {
  const [available, setAvailable] = useState(false);
  useEffect(() => {
    let live = true;
    void askOnce().then((value) => {
      if (live) setAvailable(value);
    });
    return () => {
      live = false;
    };
  }, []);
  return available;
}
