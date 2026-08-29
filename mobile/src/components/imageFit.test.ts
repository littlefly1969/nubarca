import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));

async function sourceOf(relativePath: string): Promise<string> {
  return readFile(join(here, relativePath), 'utf8');
}

test('AuthedImage accepts an optional resize mode and forwards it to Image', async () => {
  const source = await sourceOf('AuthedImage.tsx');
  assert.match(source, /resizeMode\?: ImageResizeMode/);
  assert.match(source, /<Image[\s\S]*?resizeMode=\{resizeMode\}/);
});

test('the full-screen photo and video poster request contain explicitly', async () => {
  const imageSlide = await sourceOf('ImageSlide.tsx');
  const videoSlide = await sourceOf('VideoSlide.tsx');
  assert.match(imageSlide, /<AuthedImage[\s\S]*?resizeMode="contain"/);
  assert.match(
    videoSlide,
    /<AuthedImage\s+path=\{slide\.posterUrl\}[\s\S]*?resizeMode="contain"/,
  );
});

test('grid tiles retain their existing crop/default behavior', async () => {
  const tile = await sourceOf('MediaTile.tsx');
  assert.doesNotMatch(tile, /resizeMode="contain"/);
});
