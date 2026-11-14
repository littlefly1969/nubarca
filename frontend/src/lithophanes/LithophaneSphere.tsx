import { useEffect, useRef, useState } from 'react';
import type { LithophanePreview } from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { spherePixels } from './lithophaneSphere';

export function LithophaneSphere({ preview }: { preview: LithophanePreview }) {
  const { t } = useI18n();
  const ref = useRef<HTMLCanvasElement>(null);
  const [pixels, setPixels] = useState<ImageData | null>(null);
  const [rotation, setRotation] = useState(0);
  const [lit, setLit] = useState(true);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    setPixels(null); setFailed(false);
    const image = new Image();
    image.onload = () => {
      const canvas = document.createElement('canvas');
      canvas.width = image.width; canvas.height = image.height;
      const ctx = canvas.getContext('2d');
      if (!ctx) { setFailed(true); return; }
      ctx.drawImage(image, 0, 0);
      setPixels(ctx.getImageData(0, 0, image.width, image.height));
    };
    image.onerror = () => setFailed(true);
    image.src = `data:image/png;base64,${preview.imageBase64}`;
    return () => { image.onload = null; image.onerror = null; };
  }, [preview]);

  useEffect(() => {
    const ctx = ref.current?.getContext('2d');
    if (!ctx || !pixels) return;
    const size = 360;
    const frame = ctx.createImageData(size, size);
    frame.data.set(spherePixels(pixels, preview, rotation, lit, size));
    ctx.putImageData(frame, 0, 0);
  }, [lit, pixels, preview, rotation]);

  return <figure className="lithophane-sphere">
    <canvas ref={ref} width={360} height={360} role="img" aria-label={t('lithophane.sphere')} />
    {failed && <p role="alert">{t('lithophane.previewUnavailable')}</p>}
    <label>{t('lithophane.rotation')}
      <input type="range" min={-180} max={180} value={rotation} onChange={(e) => setRotation(Number(e.target.value))} />
    </label>
    <label className="lithophane-check"><input type="checkbox" checked={lit} onChange={(e) => setLit(e.target.checked)} />{t('lithophane.illuminated')}</label>
    <figcaption>{t('lithophane.previewHint')}</figcaption>
  </figure>;
}
