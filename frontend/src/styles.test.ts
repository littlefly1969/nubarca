import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import postcss from 'postcss';
import { describe, expect, it } from 'vitest';

// Structural validation of the stylesheet.
//
// This exists because a stray `}` shipped: browsers and esbuild both RECOVER
// from an unexpected closing brace by discarding it, so `vite build` succeeded
// and every rendered-DOM test passed while the file was syntactically invalid.
// Nothing in the pipeline was actually asserting that the CSS parses.
//
// PostCSS is the parser Vite already uses for this stylesheet, so validating
// here validates with the same grammar that processes it in the build — no new
// toolchain, and it reports the exact line and column.

const here = dirname(fileURLToPath(import.meta.url));
const CSS_PATH = resolve(here, 'styles.css');
const CSS = readFileSync(CSS_PATH, 'utf8');

/** Blank comments while preserving newlines, so reported lines stay true. */
function withoutComments(css: string): string {
  return css.replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, ' '));
}

const MAIN = readFileSync(resolve(here, 'main.tsx'), 'utf8');

// The approved typography contract:
//   Space Grotesk  headings / display   500, 600, 700
//   Exo 2          UI / body / labels   400, 500, 600
// Monospace is a separate family and is unaffected.
const SPACE_GROTESK_WEIGHTS = [500, 600, 700];
const EXO_2_WEIGHTS = [400, 500, 600];

describe('typography contract', () => {
  const imports = [...MAIN.matchAll(/@fontsource\/([a-z0-9-]+)\/latin-(\d{3})\.css/g)]
    .map(([, family, weight]) => ({ family, weight: Number(weight) }));

  it('imports exactly the approved faces, and nothing else', () => {
    const sg = imports.filter((i) => i.family === 'space-grotesk').map((i) => i.weight).sort();
    const exo = imports.filter((i) => i.family === 'exo-2').map((i) => i.weight).sort();
    expect(sg).toEqual(SPACE_GROTESK_WEIGHTS);
    expect(exo).toEqual(EXO_2_WEIGHTS);
    // No third family sneaks in alongside them.
    expect([...new Set(imports.map((i) => i.family))].sort()).toEqual(['exo-2', 'space-grotesk']);
  });

  it('does not import Exo 2 700', () => {
    // The regression this guards: every UI element that wanted 650/700/750/800
    // now declares an approved weight and the UA bold on <strong>/<b>/<th>/
    // <optgroup> is normalised to 600, so a 700 face would only re-enable
    // silent nearest-weight substitution.
    expect(MAIN).not.toContain('exo-2/latin-700');
  });

  it('loads every face from our own bundle, never a CDN', () => {
    expect(MAIN).not.toMatch(/https?:\/\//);
    expect(imports.length).toBeGreaterThan(0);
  });

  it('declares no font-weight the imported faces do not ship', () => {
    // Headings resolve to Space Grotesk and the plate readout is explicitly
    // monospace; everything else in this stylesheet is Exo 2.
    const SPACE_GROTESK_SELECTORS = [
      '.app-brand', '.app-brand-lockup .app-brand', '.app-nav__group-title',
      '.metadata-action-group__title', '.similar-explorer-title', '.ws-header-title',
    ];
    const root = postcss.parse(CSS, { from: CSS_PATH });
    const offenders: string[] = [];
    root.walkDecls('font-weight', (decl) => {
      const weight = Number(decl.value.trim());
      if (!Number.isFinite(weight)) return;
      const selector = (decl.parent as { selector?: string }).selector ?? '';
      // A rule that sets its own monospace family is a different font entirely,
      // so the Exo 2 / Space Grotesk weight lists do not apply to it.
      const rule = String(decl.parent);
      if (/font-family:\s*ui-monospace/.test(rule)) return;
      const allowed = SPACE_GROTESK_SELECTORS.some((s) => selector.includes(s))
        ? SPACE_GROTESK_WEIGHTS
        : EXO_2_WEIGHTS;
      if (!allowed.includes(weight)) {
        offenders.push(`${selector} { font-weight: ${weight} } (allowed: ${allowed.join('/')})`);
      }
    });
    expect(offenders, offenders.join('\n')).toEqual([]);
  });

  it('normalises the user-agent bold elements to an Exo 2 weight', () => {
    // Without this rule <strong>, <b>, <th> and <optgroup> compute to 700 in the
    // UI font with no declaration anywhere to find by grepping.
    const root = postcss.parse(CSS, { from: CSS_PATH });
    let found = false;
    root.walkRules((rule) => {
      const parts = rule.selector.split(',').map((s) => s.trim());
      if (!['strong', 'b', 'th', 'optgroup'].every((t) => parts.includes(t))) return;
      const decl = rule.nodes.find((n) => n.type === 'decl' && n.prop === 'font-weight');
      expect(EXO_2_WEIGHTS).toContain(Number((decl as { value: string }).value));
      found = true;
    });
    expect(found, 'no strong/b/th/optgroup weight normalisation rule found').toBe(true);
  });
});

describe('styles.css parses', () => {
  it('has no CSS syntax error', () => {
    // postcss.parse throws CssSyntaxError with file/line/column on any parse
    // failure — an unexpected `}`, an unclosed block, a malformed at-rule.
    expect(() => postcss.parse(CSS, { from: CSS_PATH })).not.toThrow();
  });

  it('rejects an unmatched closing brace', () => {
    // Proves the check above can actually fail: this is the exact defect that
    // reached the branch, reproduced against the real stylesheet.
    const broken = CSS.replace(
      '@media (max-width: 26.25rem) {\n  .app-topbar__brand .app-brand {\n    display: none;\n  }\n}',
      '@media (max-width: 26.25rem) {\n  .app-topbar__brand .app-brand {\n    display: none;\n  }\n}\n}',
    );
    expect(broken, 'the fixture anchor no longer matches the stylesheet').not.toBe(CSS);
    expect(() => postcss.parse(broken, { from: CSS_PATH })).toThrow(/Unexpected \}/);
  });

  it('rejects an unclosed block', () => {
    expect(() => postcss.parse('.a { color: red;', { from: 'x.css' })).toThrow();
  });

  it('balances every brace', () => {
    // A second, independent check: PostCSS could in principle tolerate a shape
    // that is still not what the author meant, and this one cannot be fooled by
    // error recovery.
    const src = withoutComments(CSS);
    let depth = 0;
    let line = 1;
    const unmatched: number[] = [];
    for (const ch of src) {
      if (ch === '\n') line += 1;
      else if (ch === '{') depth += 1;
      else if (ch === '}') {
        depth -= 1;
        if (depth < 0) {
          unmatched.push(line);
          depth = 0;
        }
      }
    }
    expect(unmatched, `unmatched closing brace(s) at line(s) ${unmatched.join(', ')}`).toEqual([]);
    expect(depth, 'unclosed block(s) at end of file').toBe(0);
  });

  it('closes every at-rule block it opens', () => {
    const root = postcss.parse(CSS, { from: CSS_PATH });
    let atRules = 0;
    root.walkAtRules((at) => {
      atRules += 1;
      // A block at-rule (@media, @supports, @keyframes) must have a body; a
      // statement at-rule (@import, @charset) must not.
      const isBlock = /^(media|supports|keyframes|layer|container|font-face)$/i.test(at.name);
      if (isBlock) expect(at.nodes, `@${at.name} ${at.params} has no block`).toBeDefined();
    });
    expect(atRules).toBeGreaterThan(0);
  });

  it('parses to a non-trivial rule tree', () => {
    // Guards against a truncated or emptied file passing the checks above.
    const root = postcss.parse(CSS, { from: CSS_PATH });
    let rules = 0;
    let decls = 0;
    root.walkRules(() => { rules += 1; });
    root.walkDecls(() => { decls += 1; });
    expect(rules).toBeGreaterThan(500);
    expect(decls).toBeGreaterThan(2000);
  });

  // UX-02 §3: ONE layout system. The shell used to centre every page in a
  // 64rem column and let media-wall pages opt out, which is how a 2560px
  // display ended up using a third of its width for a grid.
  it('gives the authenticated main region the available width', () => {
    const root = postcss.parse(CSS, { from: CSS_PATH });
    let appMain: postcss.Rule | undefined;
    root.walkRules('.app-main', (rule) => {
      // The base rule, not a @media override.
      if (rule.parent?.type === 'root') appMain = rule;
    });
    expect(appMain, '.app-main base rule').toBeDefined();

    const decls = new Map<string, string>();
    appMain!.walkDecls((d) => { decls.set(d.prop, d.value); });

    // No imposed reading measure, and no centring that implies one.
    expect(decls.has('max-width')).toBe(false);
    expect(decls.get('margin')).toBeUndefined();
    expect(decls.get('width')).toBe('100%');
    // Responsive gutters rather than a fixed pad.
    expect(decls.get('padding-inline')).toMatch(/^clamp\(/);
    // Flex children must be able to shrink, or a wide grid pushes the page.
    expect(decls.get('min-width')).toBe('0');
  });

  it('keeps no second, conflicting full-width system', () => {
    // .app-main--media was the media-wall opt-out. One system now.
    expect(CSS).not.toContain('app-main--media');
  });

  it('offers a LOCAL reading measure for single-column forms', () => {
    const root = postcss.parse(CSS, { from: CSS_PATH });
    let measure: postcss.Rule | undefined;
    root.walkRules('.form-measure', (rule) => { measure = rule; });
    expect(measure, '.form-measure').toBeDefined();
    const decls = new Map<string, string>();
    measure!.walkDecls((d) => { decls.set(d.prop, d.value); });
    // Bounded, but never wider than its container on a phone.
    expect(decls.get('width')).toMatch(/^min\(100%,/);
  });

  it('never lets the document itself scroll sideways', () => {
    const root = postcss.parse(CSS, { from: CSS_PATH });
    let body: postcss.Rule | undefined;
    root.walkRules((rule) => {
      if (rule.selector === '.app-shell__body' && rule.parent?.type === 'root') body = rule;
    });
    expect(body, '.app-shell__body').toBeDefined();
    const decls = new Map<string, string>();
    body!.walkDecls((d) => { decls.set(d.prop, d.value); });
    expect(decls.get('max-width')).toBe('100%');
  });
});
