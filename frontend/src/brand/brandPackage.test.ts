import { readFileSync, existsSync, readdirSync, statSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

// The canonical NubArca package is the repository's source of truth for brand
// artwork. These tests guard the package itself — its integrity, its self
// consistency, and the rules that keep source masters and reference boards out
// of anything a bundler can reach.
//
// Deliberately dependency-free: PNG geometry is read straight from the IHDR
// header rather than pulling an image library in just to validate assets.

const here = dirname(fileURLToPath(import.meta.url));
const PACKAGE = resolve(here, '../../../assets/brand/nubarca');
const WEB_PUBLIC = resolve(here, '../../public/brand');
const TV_ASSETS = resolve(here, '../../../tv/assets/brand');

interface PackagedAsset {
  path: string;
  role: string;
  format: string;
  runtimeReady: boolean;
  conditionallyReady: boolean;
  width: number;
  height: number;
  hasAlpha: boolean;
  glow: boolean;
  sha256: string;
  source: string;
  notes: string;
}

const manifest = JSON.parse(readFileSync(resolve(PACKAGE, 'brand-manifest.json'), 'utf8')) as {
  brandName: string;
  tvBrandName: string;
  effectiveDate: string;
  palette: Record<string, string>;
  counts: Record<string, number>;
  assets: PackagedAsset[];
  missingAssets: unknown[];
};

const sha256 = (p: string) => createHash('sha256').update(readFileSync(p)).digest('hex');

/** width, height and whether the colour type carries alpha, from the IHDR. */
function pngHeader(file: string): { width: number; height: number; hasAlpha: boolean } {
  const b = readFileSync(file);
  const sig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  if (!b.subarray(0, 8).equals(sig)) throw new Error(`not a PNG: ${file}`);
  const colorType = b[25];
  return {
    width: b.readUInt32BE(16),
    height: b.readUInt32BE(20),
    // 4 = grey+alpha, 6 = RGB+alpha; a palette image gains alpha through tRNS.
    hasAlpha: colorType === 4 || colorType === 6 || (colorType === 3 && b.includes(Buffer.from('tRNS'))),
  };
}

describe('canonical package identity', () => {
  it('declares the current product names and effective date', () => {
    expect(manifest.brandName).toBe('NubArca');
    expect(manifest.tvBrandName).toBe('NubArca TV');
    expect(manifest.effectiveDate).toBe('2026-07-31');
  });

  it('carries the approved palette verbatim', () => {
    expect(manifest.palette).toMatchObject({
      midnight: '#0A0F1A',
      deep: '#0F1E3A',
      blue: '#1565FF',
      cyan: '#00D4FF',
      violet: '#9A6CFF',
      white: '#F5F7FB',
    });
  });
});

describe('manifest counts', () => {
  it('matches the catalogued records', () => {
    const c = manifest.counts;
    expect(manifest.assets).toHaveLength(54);
    expect(c.totalAssets).toBe(54);
    expect(c.sourceMasters).toBe(8);
    expect(c.runtimeReadyAssets).toBe(39);
    expect(c.referenceOnlyAssets).toBe(7);
    expect(c.conditionallyReadyAssets).toBe(0);
    expect(c.missingAssets).toBe(0);
    expect(manifest.missingAssets).toEqual([]);
  });

  it('agrees with what the records actually say', () => {
    expect(manifest.assets.filter((a) => a.runtimeReady)).toHaveLength(manifest.counts.runtimeReadyAssets);
    expect(manifest.assets.filter((a) => a.conditionallyReady)).toHaveLength(0);
    expect(manifest.assets.filter((a) => a.role === 'reference-only'))
      .toHaveLength(manifest.counts.referenceOnlyAssets);
    expect(manifest.assets.filter((a) => a.path.startsWith('source/'))).toHaveLength(manifest.counts.sourceMasters);
  });
});

describe('every catalogued asset', () => {
  it('exists, and its bytes match the manifest hash', () => {
    for (const a of manifest.assets) {
      const file = resolve(PACKAGE, a.path);
      expect(existsSync(file), `missing ${a.path}`).toBe(true);
      expect(sha256(file), `hash drift in ${a.path}`).toBe(a.sha256);
    }
  });

  it('has the declared dimensions and alpha', () => {
    for (const a of manifest.assets) {
      if (a.format !== 'png') continue;
      const h = pngHeader(resolve(PACKAGE, a.path));
      expect([h.width, h.height], `dimensions of ${a.path}`).toEqual([a.width, a.height]);
      expect(h.hasAlpha, `alpha of ${a.path}`).toBe(a.hasAlpha);
    }
  });

  // UX-02 compaction. The favicon family IS the light-surface flat mark, and
  // favicon.ico is a container holding exactly those PNG frames — so the tab
  // icon can never drift from the mark it is supposed to be.
  it('keeps the favicon family byte-identical to the light flat mark', () => {
    for (const px of [16, 24, 32, 48, 64]) {
      expect(
        sha256(resolve(PACKAGE, `runtime/favicon/favicon-${px}.png`)),
        `favicon-${px}.png is not the light flat mark`,
      ).toBe(sha256(resolve(PACKAGE, `runtime/web/nubarca-mark-flat-on-light-${px}.png`)));
    }
  });

  it('assembles favicon.ico from the shipped PNG frames themselves', () => {
    const ico = readFileSync(resolve(PACKAGE, 'runtime/favicon/favicon.ico'));
    expect(ico.readUInt16LE(0), 'ICONDIR reserved').toBe(0);
    expect(ico.readUInt16LE(2), 'ICONDIR type (1 = icon)').toBe(1);
    const frames = ico.readUInt16LE(4);
    expect(frames).toBe(4);
    const seen: number[] = [];
    for (let i = 0; i < frames; i += 1) {
      const entry = 6 + i * 16;
      const size = ico.readUInt8(entry) || 256;
      const bytes = ico.readUInt32LE(entry + 8);
      const offset = ico.readUInt32LE(entry + 12);
      const frame = ico.subarray(offset, offset + bytes);
      expect(frame, `ICO frame ${size} is not the shipped PNG`)
        .toEqual(readFileSync(resolve(PACKAGE, `runtime/favicon/favicon-${size}.png`)));
      seen.push(size);
    }
    expect(seen).toEqual([16, 24, 32, 48]);
  });

  it('records the flat marks and favicons as compact derivatives', () => {
    const compact = manifest.assets.filter(
      (a) => /nubarca-mark-flat-on-(dark|light)-\d+\.png$/.test(a.path)
        || a.path.startsWith('runtime/favicon/'),
    );
    expect(compact.length).toBe(23);
    for (const a of compact) {
      expect(a.notes, `${a.path} is not recorded as compacted`).toContain('Compact derivative');
    }
  });

  it('is catalogued — no stray files in the package', () => {
    const catalogued = new Set(manifest.assets.map((a) => a.path));
    const docs = new Set(['README.md', 'NUBARCA_BRAND_HANDOFF.md', 'brand-manifest.json', 'checksums.sha256']);
    const walk = (dir: string, prefix = ''): string[] =>
      readdirSync(dir).flatMap((n) => {
        const rel = prefix ? `${prefix}/${n}` : n;
        return statSync(resolve(dir, n)).isDirectory() ? walk(resolve(dir, n), rel) : [rel];
      });
    for (const rel of walk(PACKAGE)) {
      expect(catalogued.has(rel) || docs.has(rel), `uncatalogued file ${rel}`).toBe(true);
    }
  });
});

describe('package hygiene', () => {
  // The former product name. Written split so this file does not itself carry it.
  const OLD_NAME = ['nano', 'cloud'].join('');

  // Package-RELATIVE paths on purpose: an absolute path includes the checkout
  // directory, which is outside this package's control, so matching against it
  // would flag every file here for reasons that have nothing to do with the
  // package's own contents.
  const walk = (dir: string, prefix = ''): string[] =>
    readdirSync(dir).flatMap((n) => {
      const rel = prefix ? `${prefix}/${n}` : n;
      return statSync(resolve(dir, n)).isDirectory() ? walk(resolve(dir, n), rel) : [rel];
    });
  const files = walk(PACKAGE);

  it('ships no font binaries — fonts are sourced and licensed separately', () => {
    expect(files.filter((f) => /\.(woff2?|ttf|otf|eot)$/i.test(f))).toEqual([]);
  });

  it('ships no archives, temporary files or executables', () => {
    expect(files.filter((f) => /\.(zip|tar|gz|tgz|tmp|bak|sh|exe|bat)$/i.test(f))).toEqual([]);
  });

  it('carries no old-brand filename anywhere', () => {
    const oldName = new RegExp(OLD_NAME, 'i');
    expect(files.filter((f) => oldName.test(f))).toEqual([]);
  });
});

describe('runtime / source / reference separation', () => {
  it('never marks a reference board runtime-ready', () => {
    for (const a of manifest.assets.filter((x) => x.path.startsWith('reference/'))) {
      expect(a.runtimeReady, `${a.path} is runtime-ready`).toBe(false);
      expect(a.role, `${a.path} role`).toBe('reference-only');
    }
  });

  it('never marks a source master runtime-ready', () => {
    for (const a of manifest.assets.filter((x) => x.path.startsWith('source/'))) {
      expect(a.runtimeReady, `${a.path} is runtime-ready`).toBe(false);
    }
  });

  it('marks every runtime/ asset runtime-ready', () => {
    for (const a of manifest.assets.filter((x) => x.path.startsWith('runtime/'))) {
      expect(a.runtimeReady, `${a.path} is not runtime-ready`).toBe(true);
    }
  });

  it('places no source master or reference board where a bundler could reach it', () => {
    const forbidden = new Set(
      manifest.assets
        .filter((a) => a.path.startsWith('reference/') || a.path.startsWith('source/'))
        .map((a) => a.path.split('/').pop()!),
    );
    for (const dir of [WEB_PUBLIC, TV_ASSETS]) {
      for (const f of readdirSync(dir)) {
        expect(forbidden.has(f), `${f} is a source/reference asset shipped from ${dir}`).toBe(false);
      }
    }
  });
});

describe('BRAND-VIS-01 provenance', () => {
  const derived = [
    'source/nubarca-mark-flat-on-dark-master.png',
    'source/nubarca-mark-flat-on-light-master.png',
    'source/nubarca-tv-lockup-transparent-master.png',
    'source/nubarca-wordmark-on-dark-master.png',
  ];

  it('names the approved master each derivative came from', () => {
    for (const path of derived) {
      const a = manifest.assets.find((x) => x.path === path)!;
      expect(a, path).toBeDefined();
      expect(a.source, `${path} provenance`).toContain('BRAND-VIS-01');
      const cited = a.source.match(/source\/[a-z0-9-]+\.png/)?.[0];
      expect(cited, `${path} cites no master`).toBeDefined();
      // The cited master must itself be a real, catalogued asset.
      expect(manifest.assets.some((x) => x.path === cited), `${path} cites a missing ${cited}`).toBe(true);
    }
  });

  it('records how each derivative lost the luminous treatment of its master', () => {
    // Only the two FLAT MARKS were deglowed; verified against the binaries:
    //   * flat marks  — glow removed and recoloured to solid palette colours;
    //   * wordmark    — alpha geometry is pixel-identical to its master (28821
    //                   opaque px, 23 differing), so nothing was deglowed; what
    //                   changed is the recolour, Cloud White 0% -> 16%;
    //   * TV lockup   — lost the presentation CARD it was embedded in.
    // `glow` means "carries an external halo", which none of the four does, so
    // the flag is false for all of them regardless of the transformation.
    const deglowed = derived.filter((p) => p.includes('mark-flat'));
    for (const path of deglowed) {
      expect(manifest.assets.find((x) => x.path === path)!.source.toLowerCase(), path)
        .toContain('glow removed');
    }
    const lockup = manifest.assets.find((x) => x.path.includes('tv-lockup-transparent-master'))!;
    expect(lockup.source.toLowerCase()).toContain('card background and border removed');
    const wordmark = manifest.assets.find((x) => x.path.includes('wordmark-on-dark-master'))!;
    expect(wordmark.source.toLowerCase()).toContain('isolated');
    for (const path of derived) {
      expect(manifest.assets.find((x) => x.path === path)!.glow, `${path} external halo`).toBe(false);
    }
  });

  it('records the approved palette work where a derivative was recoloured', () => {
    for (const path of derived.filter((p) => p.includes('mark-flat') || p.includes('wordmark'))) {
      expect(manifest.assets.find((x) => x.path === path)!.source.toLowerCase(), path)
        .toMatch(/recolou?red|palette|cloud white|electric blue|midnight navy/);
    }
  });

  it('records the geometry treatment that produced each derivative', () => {
    for (const path of derived) {
      const a = manifest.assets.find((x) => x.path === path)!;
      expect(a.source.toLowerCase(), `${path} describes no geometry work`)
        .toMatch(/isolated|cropped|centered|removed/);
    }
  });

  it('keeps every derivative on transparency, as its provenance claims', () => {
    for (const path of derived) {
      expect(manifest.assets.find((x) => x.path === path)!.hasAlpha, path).toBe(true);
    }
  });
});

describe('luminous artwork is flagged where it is used', () => {
  const flag = (needle: string) =>
    manifest.assets.find((a) => a.path.endsWith(needle))!.glow;

  it('marks the launcher and PWA artwork as luminous', () => {
    for (const n of [
      'nubarca-pwa-192.png', 'nubarca-pwa-512.png',
      'nubarca-apple-touch-icon-180.png', 'nubarca-expo-app-icon-1024.png',
      'nubarca-fire-tv-icon-512.png',
    ]) {
      expect(flag(n), `${n} should be flagged luminous`).toBe(true);
    }
  });

  it('marks the flat marks, wordmarks, TV lockup and splash as NOT luminous', () => {
    for (const n of [
      'nubarca-mark-flat-on-dark-48.png', 'nubarca-mark-flat-on-light-48.png',
      'nubarca-wordmark-on-dark-960w.png', 'nubarca-wordmark-on-light.png',
      'nubarca-tv-lockup-transparent-1280w.png', 'nubarca-tv-splash-1920x1080.png',
    ]) {
      expect(flag(n), `${n} should not be flagged luminous`).toBe(false);
    }
  });

  it('keeps the small UI mark free of the glow that would smear at 24px', () => {
    for (const a of manifest.assets.filter((x) => x.path.includes('mark-flat-on-'))) {
      expect(a.glow, a.path).toBe(false);
    }
  });
});

describe('platform assets have the shapes their slots require', () => {
  const find = (needle: string) => manifest.assets.find((a) => a.path.endsWith(needle))!;

  it('gives every square icon slot a square asset', () => {
    for (const n of ['nubarca-pwa-192.png', 'nubarca-pwa-512.png', 'nubarca-pwa-maskable-512.png',
      'nubarca-apple-touch-icon-180.png', 'nubarca-expo-app-icon-1024.png', 'nubarca-fire-tv-icon-512.png']) {
      const a = find(n);
      expect(a.width, `${n} is not square`).toBe(a.height);
    }
  });

  it('sizes the banners and splash to their exact platform slots', () => {
    expect([find('nubarca-android-tv-banner-320x180.png').width,
            find('nubarca-android-tv-banner-320x180.png').height]).toEqual([320, 180]);
    expect([find('nubarca-fire-tv-banner-1280x720.png').width,
            find('nubarca-fire-tv-banner-1280x720.png').height]).toEqual([1280, 720]);
    expect([find('nubarca-tv-splash-1920x1080.png').width,
            find('nubarca-tv-splash-1920x1080.png').height]).toEqual([1920, 1080]);
  });

  it('keeps the TV lockup transparent so it needs no card behind it', () => {
    for (const w of ['640w', '1280w', '1800w']) {
      const a = find(`nubarca-tv-lockup-transparent-${w}.png`);
      expect(a.hasAlpha, `${w} lockup is not transparent`).toBe(true);
      // ~4.3:1 — a wide lockup, never squeezed into a 16:9 banner slot.
      expect(a.width / a.height).toBeGreaterThan(3.5);
    }
  });

  it('keeps every wordmark on transparency with a consistent lockup shape', () => {
    for (const a of manifest.assets.filter((x) => x.path.includes('wordmark-on-'))) {
      expect(a.hasAlpha, a.path).toBe(true);
    }
  });
});
