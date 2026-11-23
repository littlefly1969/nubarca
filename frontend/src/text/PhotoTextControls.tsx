import { useEffect, useState } from 'react';
import { DEFAULT_PHOTO_TEXT, type PhotoTextLayout } from '@nubarca/contracts';
import { useI18n } from '../i18n';
import './photoText.css';

export function PhotoTextControls({ value, onChange }: {
  value: PhotoTextLayout; onChange(value: PhotoTextLayout): void;
}) {
  const { t } = useI18n();
  return <fieldset className="photo-text-controls" data-testid="photo-text-controls">
    <legend>{t('photoText.layout')}</legend>
    <label>{t('photoText.mode')}
      <select value={value.mode} onChange={(e) => onChange({ ...value, mode: e.target.value as PhotoTextLayout['mode'] })}>
        {(['below', 'overlay', 'bubble'] as const).map((mode) =>
          <option key={mode} value={mode}>{t(`photoText.${mode}`)}</option>)}
      </select>
    </label>
    {value.mode !== 'below' && <>
      {(['x', 'y', 'width', 'height'] as const).map((key) => <label key={key}>{t(`photoText.${key}`)}
        <PositionNumber min={key === 'width' ? 20 : key === 'height' ? 15 : 0}
          max={key === 'width' ? 95 : key === 'height' ? 80 : 100}
          value={Math.round(value[key] * 100)} testId={`photo-text-${key}`}
          onChange={(n) => onChange({ ...value, [key]: n / 100 })} />
      </label>)}
      {value.mode === 'bubble' && <label>{t('photoText.tail')}
        <select value={value.tail} onChange={(e) => onChange({ ...value, tail: e.target.value as 'left' | 'right' })}>
          <option value="left">{t('photoText.left')}</option><option value="right">{t('photoText.right')}</option>
        </select>
      </label>}
      <button type="button" onClick={() => onChange({ ...DEFAULT_PHOTO_TEXT, mode: value.mode })}>{t('photoText.center')}</button>
      <p>{t('photoText.help')}</p>
    </>}
  </fieldset>;
}

function PositionNumber({ value, min, max, testId, onChange }: {
  value: number; min: number; max: number; testId: string; onChange(value: number): void;
}) {
  const [draft, setDraft] = useState(String(value));
  useEffect(() => setDraft(String(value)), [value]);
  return <input type="number" min={min} max={max} step={1} value={draft} data-testid={testId}
    onChange={(e) => {
      setDraft(e.target.value);
      const next = e.target.valueAsNumber;
      if (Number.isFinite(next) && next >= min && next <= max) onChange(next);
    }} onBlur={() => setDraft(String(value))} />;
}
