import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { FaceCrop } from './FaceCrop';
import { I18nProvider } from '../../i18n';

afterEach(() => cleanup());

const box = { x: 0.1, y: 0.1, width: 0.2, height: 0.2 };

it('uses the server face-preview URL by default', () => {
  render(<I18nProvider><FaceCrop faceId="face-1" fileItemId="file-1" box={box} /></I18nProvider>);
  const img = screen.getByRole('img');
  expect(img.getAttribute('src')).toBe('/api/people/faces/face-1/preview?size=small');
});

it('falls back to the medium preview then the small thumbnail on error', () => {
  render(<I18nProvider><FaceCrop faceId="face-1" fileItemId="file-1" box={box} /></I18nProvider>);

  // preview 404 → medium CSS crop
  fireEvent.error(screen.getByRole('img'));
  expect(screen.getByRole('img').getAttribute('src')).toBe('/api/files/file-1/preview');

  // medium fails → small thumbnail
  fireEvent.error(screen.getByRole('img'));
  expect(screen.getByRole('img').getAttribute('src')).toBe('/api/files/file-1/thumbnail?size=small');

  // small fails → placeholder (no img)
  fireEvent.error(screen.getByRole('img'));
  expect(screen.queryByRole('img')).toBeNull();
});

it('displays the server preview directly without a second bbox crop', () => {
  const { container } = render(<I18nProvider><FaceCrop faceId="face-1" fileItemId="file-1" box={box} /></I18nProvider>);
  const img = screen.getByRole('img') as HTMLImageElement;
  // Server crop is already square → fill the square container, cover. No CSS
  // bounding-box crop (no negative offsets / >100% sizing) is applied to it.
  expect(img.style.width).toBe('100%');
  expect(img.style.height).toBe('100%');
  expect(img.style.objectFit).toBe('cover');
  expect(img.style.left).toBe('');
  expect(img.style.top).toBe('');
  // Dev-safe marker: which source rendered (server preview here).
  expect(container.querySelector('.face-crop')?.getAttribute('data-stage')).toBe('preview');
});

it('applies a CSS bbox crop only on the fallback preview, not the server crop', () => {
  const { container } = render(<I18nProvider><FaceCrop faceId="face-1" fileItemId="file-1" box={box} /></I18nProvider>);
  // Drop to the medium-preview fallback.
  fireEvent.error(screen.getByRole('img'));
  const img = screen.getByRole('img') as HTMLImageElement;
  expect(img.getAttribute('src')).toBe('/api/files/file-1/preview');
  // The fallback IS a CSS crop: sized >100% and offset negative to frame the box.
  expect(img.style.position).toBe('absolute');
  expect(parseFloat(img.style.width)).toBeGreaterThan(100);
  expect(container.querySelector('.face-crop')?.getAttribute('data-stage')).toBe('medium');
});

it('renders a clickable button when onClick is provided', () => {
  const onClick = vi.fn();
  render(<I18nProvider><FaceCrop faceId="face-1" fileItemId="file-1" box={box} onClick={onClick} alt="Volto" /></I18nProvider>);
  fireEvent.click(screen.getByRole('button', { name: 'Volto' }));
  expect(onClick).toHaveBeenCalledOnce();
});

it('does not render any storage internals', () => {
  const { container } = render(<I18nProvider><FaceCrop faceId="face-1" fileItemId="file-1" box={box} /></I18nProvider>);
  const html = container.innerHTML;
  for (const needle of ['blobObjectId', 'storageKey', 'sha256', '/storage/objects/']) {
    expect(html).not.toContain(needle);
  }
});
