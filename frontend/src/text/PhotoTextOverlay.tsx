import { useLayoutEffect, useRef, type CSSProperties, type PointerEvent } from 'react';
import { photoTextRect, type PhotoTextLayout } from '@nubarca/contracts';
import { useI18n } from '../i18n';
import './photoText.css';

/** Shared by the editor and the saved composition. Text is always plain text. */
export function PhotoTextOverlay({ text, author, layout, font, onChange }: {
  text: string; author?: string; layout: PhotoTextLayout;
  font?: { family: string; weight: number }; onChange?(value: PhotoTextLayout): void;
}) {
  const { t } = useI18n();
  const ref = useRef<HTMLDivElement>(null);
  const words = useRef<HTMLDivElement>(null);
  const drag = useRef<{ id: number; px: number; py: number; x: number; y: number } | null>(null);
  useLayoutEffect(() => {
    const box = ref.current; const content = words.current;
    if (!box || !content) return;
    const fit = () => {
      let size = box.clientWidth * 0.075;
      content.style.fontSize = `${size}px`;
      while (size > 1 && (content.scrollHeight > content.clientHeight + 1 || content.scrollWidth > content.clientWidth + 1)) {
        size *= 0.9; content.style.fontSize = `${size}px`;
      }
    };
    fit();
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(fit);
    observer?.observe(box);
    document.fonts?.ready.then(fit);
    return () => observer?.disconnect();
  }, [text, author, layout.width, layout.height, layout.mode, font?.family]);
  if (layout.mode === 'below') return null;
  const rect = photoTextRect(layout);
  const move = (e: PointerEvent<HTMLDivElement>) => {
    const start = drag.current; const parent = ref.current?.parentElement?.getBoundingClientRect();
    if (!start || !parent || start.id !== e.pointerId || !onChange || !parent.width || !parent.height) return;
    const clamp = (v: number) => Math.max(0, Math.min(1, v));
    onChange({ ...layout, x: clamp(start.x + (e.clientX - start.px) / (parent.width * (1 - layout.width))),
      y: clamp(start.y + (e.clientY - start.py) / (parent.height * (1 - layout.height))) });
  };
  return <div ref={ref} className={`photo-text-overlay photo-text-overlay--${layout.mode}`}
    data-testid="photo-text-overlay" data-tail={layout.tail}
    style={{ left: `${rect.left * 100}%`, top: `${rect.top * 100}%`, width: `${rect.width * 100}%`, height: `${rect.height * 100}%`,
      fontFamily: font?.family, fontWeight: font?.weight } as CSSProperties}
    role={onChange ? 'group' : undefined} tabIndex={onChange ? 0 : undefined}
    aria-label={onChange ? t('photoText.drag') : undefined}
    onPointerDown={onChange ? (e) => {
      if (e.button !== 0) return;
      e.preventDefault(); e.stopPropagation(); e.currentTarget.focus(); e.currentTarget.setPointerCapture(e.pointerId);
      drag.current = { id: e.pointerId, px: e.clientX, py: e.clientY, x: layout.x, y: layout.y };
    } : undefined}
    onPointerMove={move} onPointerUp={() => { drag.current = null; }} onPointerCancel={() => { drag.current = null; }} onLostPointerCapture={() => { drag.current = null; }}
    onKeyDown={onChange ? (e) => {
      const dx = e.key === 'ArrowLeft' ? -0.01 : e.key === 'ArrowRight' ? 0.01 : 0;
      const dy = e.key === 'ArrowUp' ? -0.01 : e.key === 'ArrowDown' ? 0.01 : 0;
      if (!dx && !dy) return;
      e.preventDefault(); e.stopPropagation();
      onChange({ ...layout, x: Math.max(0, Math.min(1, layout.x + dx)), y: Math.max(0, Math.min(1, layout.y + dy)) });
    } : undefined}>
    {layout.mode === 'bubble' && <svg className="photo-text-bubble" viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true">
      <path d={layout.tail === 'left' ? 'M1 1 H99 V84 H32 L16 98 L20 84 H1 Z' : 'M1 1 H99 V84 H80 L84 98 L68 84 H1 Z'} />
    </svg>}
    <div className="photo-text-overlay-words" ref={words}><span>{text}</span>{author && <span className="photo-text-signature">— {author}</span>}</div>
  </div>;
}
