// Calendar validation for the on-screen date keyboard.
//
// A 'YYYY-MM-DD' string can be well-formed and still not be a date: 2026-02-30
// and 2026-13-01 both pass a regular expression. Round-tripping through Date
// and comparing the parts back is what rejects them — JavaScript silently
// normalizes an overflowing day into the next month, so an unchanged
// round-trip is the actual proof the date exists.
export function isValidDateInput(value: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;
  const [year, month, day] = value.split('-').map(Number);
  if (month < 1 || month > 12 || day < 1) return false;
  const date = new Date(Date.UTC(year, month - 1, day));
  return date.getUTCFullYear() === year
    && date.getUTCMonth() === month - 1
    && date.getUTCDate() === day;
}
