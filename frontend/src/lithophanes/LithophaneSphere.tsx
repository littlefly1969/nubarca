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
  const [printing, setPrinting] = useState(false);
  const [expanded, setExpanded] = useState(false);
  const drag = useRef<{ id: number; x: number; rotation: number } | null>(null);

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
    frame.data.set(spherePixels(pixels, preview, rotation, lit, size, printing));
    ctx.putImageData(frame, 0, 0);
  }, [lit, pixels, preview, rotation, printing]);

  return <figure className={`lithophane-sphere${expanded ? ' lithophane-sphere-expanded' : ''}`}>
    <button type="button" className="btn btn-secondary" aria-expanded={expanded} onClick={() => setExpanded((old) => !old)}>{t(expanded ? 'lithophane.previewShrink' : 'lithophane.previewExpand')}</button>
    <canvas ref={ref} width={360} height={360} role="img" aria-label={t('lithophane.sphere')}
      onPointerDown={(e) => { drag.current = { id: e.pointerId, x: e.clientX, rotation }; e.currentTarget.setPointerCapture(e.pointerId); }}
      onPointerMove={(e) => { const start = drag.current; if (start?.id === e.pointerId) {
        const angle = start.rotation + 360 * (e.clientX - start.x) / Math.max(1, e.currentTarget.clientWidth);
        setRotation(((angle + 180) % 360 + 360) % 360 - 180);
      } }}
      onPointerUp={() => { drag.current = null; }} onPointerCancel={() => { drag.current = null; }}
      onLostPointerCapture={() => { drag.current = null; }} />
    {failed && <p role="alert">{t('lithophane.previewUnavailable')}</p>}
    {preview.hanging && <label>{t('lithophane.useMode')}
      <select value={printing ? 'print' : 'use'} onChange={(e) => setPrinting(e.target.value === 'print')}>
        <option value="use">{t('lithophane.previewUse')}</option>
        <option value="print">{t('lithophane.previewPrint')}</option>
      </select>
    </label>}
    <label>{t('lithophane.rotation')}
      <input type="range" min={-180} max={180} value={rotation} onChange={(e) => setRotation(Number(e.target.value))} />
    </label>
    <label className="lithophane-check"><input type="checkbox" checked={lit} onChange={(e) => setLit(e.target.checked)} />{t('lithophane.illuminated')}</label>
    <figcaption>{t('lithophane.previewHint')}</figcaption>
  </figure>;
}
