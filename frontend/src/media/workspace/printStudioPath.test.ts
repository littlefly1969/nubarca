import { describe, expect, it } from 'vitest';
import { PRINT_SELECTION_MAX } from '@nubarca/contracts';
import { printStudioPath } from './printStudioPath';

const image = (id: string) => ({ id, kind: 'image' });
const video = (id: string) => ({ id, kind: 'video' });

describe('printStudioPath', () => {
  it('takes the photographs in the order selected, and counts the videos left out', () => {
    expect(printStudioPath(['b', 'v', 'a'], [image('a'), image('b'), video('v')], null))
      .toBe('/print?files=b%2Ca&videos=1');
  });

  it('names the album it came from', () => {
    expect(printStudioPath(['a'], [image('a')], 'al1')).toBe('/print?album=al1&files=a');
  });

  it('takes as many photographs as one print session holds, and no more', () => {
    const ids = (n: number) => Array.from({ length: n }, (_, i) => `p${i}`);
    expect(printStudioPath(ids(PRINT_SELECTION_MAX), ids(PRINT_SELECTION_MAX).map(image), null)).not.toBeNull();
    expect(printStudioPath(ids(PRINT_SELECTION_MAX + 1), ids(PRINT_SELECTION_MAX + 1).map(image), null)).toBeNull();
  });
});
