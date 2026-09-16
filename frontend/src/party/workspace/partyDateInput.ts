// A `datetime-local` input speaks local wall time with no zone; the wire speaks
// UTC. Converting in ONE place at each boundary is what keeps "ends at 23:00"
// from drifting an hour every time a form is opened.

export function toLocalInput(iso: string | null | undefined): string {
  if (!iso) return '';
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}`
    + `T${pad(at.getHours())}:${pad(at.getMinutes())}`;
}

export function fromLocalInput(value: string): string | null {
  if (value === '') return null;
  const at = new Date(value);
  return Number.isNaN(at.getTime()) ? null : at.toISOString();
}
