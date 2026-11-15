import { useI18n } from '../i18n';
import { TEXT_FONTS, type TextFontId } from './textFonts';

/** The faces for the owner's own words, each written in itself. */
export function TextFontPicker({ value, onChange, disabled, name, testId }: {
  value: TextFontId;
  onChange: (font: TextFontId) => void;
  disabled?: boolean;
  /** The radio group's name; unique per form. */
  name: string;
  testId?: string;
}) {
  const { t } = useI18n();
  return <fieldset className="text-font-picker" disabled={disabled} data-testid={testId}>
    <legend>{t('textFont.legend')}</legend>
    {TEXT_FONTS.map((font) => <label key={font.id} style={{ fontFamily: font.family, fontWeight: font.weight }}>
      <input type="radio" name={name} value={font.id} checked={value === font.id}
        onChange={() => onChange(font.id)} />
      {t(font.label)}
    </label>)}
  </fieldset>;
}
