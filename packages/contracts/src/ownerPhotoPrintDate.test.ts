import { test } from 'node:test';
import assert from 'node:assert/strict';
import { formatOwnerPrintDate, OWNER_PRINT_DATE_FORMATS } from './ownerPhotoPrintDate.ts';

// The table the server prints with (OwnerPhotoPrintDates.Formats), value for value.
test('one deterministic format per language', () => {
  assert.deepEqual(OWNER_PRINT_DATE_FORMATS, {
    it: 'dd/MM/yyyy', es: 'dd/MM/yyyy', de: 'dd.MM.yyyy', en: 'MM/dd/yyyy',
  });
  assert.equal(formatOwnerPrintDate('2026-10-02', 'it'), '02/10/2026');
  assert.equal(formatOwnerPrintDate('2026-10-02', 'es'), '02/10/2026');
  assert.equal(formatOwnerPrintDate('2026-10-02', 'de'), '02.10.2026');
  assert.equal(formatOwnerPrintDate('2026-10-02', 'en'), '10/02/2026');
});

test('nothing that is not a calendar day becomes text', () => {
  for (const bad of ['', '2026-10-2', '2026/10/02', '2026-10-02T00:00:00Z', 'oggi']) {
    assert.equal(formatOwnerPrintDate(bad, 'it'), null, bad);
  }
  assert.equal(formatOwnerPrintDate('2026-10-02', 'fr' as never), null);
});
