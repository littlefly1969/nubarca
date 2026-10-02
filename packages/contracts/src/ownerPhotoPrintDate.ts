// THE DATE ON AN OWNER'S DIRECT PRINT, as text.
//
// The server prints it (OwnerPhotoPrintDates.Format) and the browser previews
// it from this one table, so the characters on the paper are the characters
// the person saw. Deliberately not Intl / CultureInfo: their output for the
// same locale differs between runtimes and versions, and a preview that says
// 02/10/2026 above a print that says 2/10/2026 is a broken promise.

export type OwnerPrintDateLocale = 'it' | 'en' | 'es' | 'de';

export const OWNER_PRINT_DATE_FORMATS: Readonly<Record<OwnerPrintDateLocale, string>> = {
  it: 'dd/MM/yyyy',
  es: 'dd/MM/yyyy',
  de: 'dd.MM.yyyy',
  en: 'MM/dd/yyyy',
};

/** Where the date came from: the owner's correction, the camera, or today. */
export type OwnerPrintDateSource = 'user' | 'embedded' | 'today' | 'none';

/**
 * `isoDate` ("yyyy-MM-dd", as the server resolves it) in `locale`'s format;
 * null for anything that is not such a date.
 */
export function formatOwnerPrintDate(isoDate: string, locale: OwnerPrintDateLocale): string | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(isoDate);
  const format = OWNER_PRINT_DATE_FORMATS[locale];
  if (!match || !format) return null;
  const [, yyyy, MM, dd] = match;
  return format.replace('yyyy', yyyy).replace('MM', MM).replace('dd', dd);
}
