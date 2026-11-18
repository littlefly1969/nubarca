import { useState } from 'react';
import type { LithophaneSettings } from '@nubarca/api-client';
import { PhotoCropFrame } from '../party/PhotoCropFrame';
import { mediumPreviewUrl } from '../components/files/types';
import { useI18n } from '../i18n';

const RATIOS = [['1', '1:1'], ['1.3333333333333333', '4:3'], ['0.75', '3:4'], ['1.7777777777777777', '16:9'], ['0.5625', '9:16']];

export function LithophanePhotoEditor({ fileId, settings, onChange: change, disabled }: {
  fileId: string;
  settings: LithophaneSettings;
  onChange: (s: LithophaneSettings) => void;
  disabled?: boolean;
}) {
  const { t, formatNumber } = useI18n();
  const onChange = (value: LithophaneSettings) => { if (!disabled) change(value); };
  const [aspect, setAspect] = useState<number | null>(null);
  const frame = settings.frameAspect ?? settings.pictureWidthDegrees / settings.pictureHeightDegrees;
  const ratioValue = String(frame);
  return <fieldset className="lithophane-crop-editor" disabled={disabled}>
    <legend>{t('lithophane.cropTitle')}</legend>
    <p className="lithophane-hint">{t('lithophane.cropHelp')}</p>
    <label>{t('lithophane.cropShape')}
      <select value={ratioValue} onChange={(e) => onChange({ ...settings,
        frameAspect: Number(e.target.value), pictureScale: settings.pictureScale ?? 0.8,
        zoom: Math.max(1, settings.zoom),
      })}>
        {!RATIOS.some(([value]) => value === ratioValue) && <option value={ratioValue}>{t('lithophane.currentRatio')}</option>}
        {RATIOS.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
        {aspect !== null && aspect >= 0.2 && aspect <= 5 && <option value={aspect}>{t('lithophane.originalRatio')}</option>}
      </select>
    </label>
    <div className="lithophane-crop-stage" style={{ maxWidth: Math.min(440, 440 * frame) }}>
      <PhotoCropFrame src={mediumPreviewUrl(fileId)} aspect={aspect ?? frame} slotAspect={frame}
        view={{ centerX: settings.centerX, centerY: settings.centerY, zoom: settings.zoom }}
        onChange={(view) => onChange({ ...settings, ...view })}
        onAspect={(width, height) => { if (width > 0 && height > 0) setAspect(width / height); }}
        label={t('lithophane.cropMove')} testId="lithophane-crop">
        <span className="lithophane-crop-guide" aria-hidden="true" />
      </PhotoCropFrame>
    </div>
    <label>{t('lithophane.cropZoom')}
      <input type="range" min={settings.frameAspect == null ? 0.05 : 1} max={4} step={0.05}
        value={settings.zoom} onChange={(e) => onChange({ ...settings, zoom: Number(e.target.value) })} />
      <output>{formatNumber(settings.zoom, { maximumFractionDigits: 2 })}×</output>
    </label>
    <button type="button" className="btn btn-secondary" onClick={() => onChange({ ...settings, centerX: 0.5, centerY: 0.5, zoom: 1 })}>
      {t('lithophane.resetCrop')}
    </button>
    <label>{t('lithophane.photoSize')}
      <input type="range" min={20} max={100} step={1} value={(settings.pictureScale ?? 0.8) * 100}
        onChange={(e) => onChange({ ...settings, frameAspect: frame, pictureScale: Number(e.target.value) / 100 })} />
      <output>{formatNumber((settings.pictureScale ?? 0.8) * 100, { maximumFractionDigits: 0 })}%</output>
    </label>
    <p className="lithophane-hint">{t('lithophane.photoSizeHelp')}</p>
  </fieldset>;
}
