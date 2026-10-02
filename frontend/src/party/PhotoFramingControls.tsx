import {
  MAX_PLACEMENT_ZOOM, PLACEMENT_ZOOM_STEP, centerPlacement, effectiveZoom, fillPlacement, fitPlacement,
  sliderMinimumZoom, type PhotoPlacement,
} from '@nubarca/contracts';
import { useI18n } from '../i18n';

// The zoom beside a PhotoCropFrame, for every editor that frames a photograph:
// "Adatta" (the whole photograph), "Riempi" (the frame covered, as it always
// was), "Centra" (the same zoom, centred), and a slider from the whole
// photograph to 4x. Zooming out is a button, not something hidden at the end
// of a slider; the slider's own minimum is contain rounded onto its step, so
// "Adatta" is how the exact contain is reached.

export function PhotoFramingControls({
  aspect, slotAspect, view, onChange, zoomLabel, testId = 'photo-framing',
}: {
  /** The photograph's own width / height. */
  aspect: number;
  /** The frame's width / height. */
  slotAspect: number;
  view: PhotoPlacement;
  onChange: (view: PhotoPlacement) => void;
  /** The slider's label, where a surface already names it. */
  zoomLabel?: string;
  testId?: string;
}) {
  const { t } = useI18n();
  const min = sliderMinimumZoom(aspect, slotAspect);
  const zoom = effectiveZoom(aspect, slotAspect, view.zoom);
  return (
    <div className="photo-framing" data-testid={testId}>
      <div className="photo-framing-presets" role="group" aria-label={t('photoFraming.presets')}>
        <button
          type="button"
          data-testid={`${testId}-fit`}
          onClick={() => onChange(fitPlacement(aspect, slotAspect))}
        >
          {t('photoFraming.fit')}
        </button>
        <button type="button" data-testid={`${testId}-fill`} onClick={() => onChange(fillPlacement())}>
          {t('photoFraming.fill')}
        </button>
        <button type="button" data-testid={`${testId}-center`} onClick={() => onChange(centerPlacement(view))}>
          {t('photoFraming.center')}
        </button>
      </div>
      <label className="photo-framing-zoom">
        <span>{zoomLabel ?? t('photoFraming.zoom')}</span>
        <input
          type="range"
          data-testid={`${testId}-zoom`}
          min={min}
          max={MAX_PLACEMENT_ZOOM}
          step={PLACEMENT_ZOOM_STEP}
          value={Math.max(min, zoom)}
          aria-valuetext={`${zoom.toFixed(2)}×`}
          onChange={(event) => onChange({ ...view, zoom: Number(event.target.value) })}
        />
      </label>
    </div>
  );
}
