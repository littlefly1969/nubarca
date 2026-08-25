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

  it('pins the TV Party QR cards at the bottom at three times their old side', () => {
    const root = postcss.parse(CSS, { from: CSS_PATH });
    const declarations = (selector: string) => {
      const values = new Map<string, string>();
      root.walkRules(selector, (rule) => {
        if (rule.parent?.type === 'root') {
          rule.walkDecls((decl) => { values.set(decl.prop, decl.value); });
        }
      });
      return values;
    };

    const corner = declarations('.tv-party-corner');
    expect(corner.get('bottom')).toBe('max(1rem, env(safe-area-inset-bottom))');
    expect(corner.has('top')).toBe(false);
    const qr = declarations('.tv-party-qr');
    expect(qr.get('width')).toBe('min(480px, calc(50vw - 2.5rem), calc(100vh - 9rem))');
    expect(qr.get('height')).toBe('min(480px, calc(50vw - 2.5rem), calc(100vh - 9rem))');
    expect(qr.get('box-sizing')).toBe('border-box');

    const compact = root.nodes.find((node): node is postcss.AtRule =>
      node.type === 'atrule'
      && node.name === 'media'
      && node.params === '(max-width: 700px), (max-height: 700px)');
    expect(compact, 'compact TV QR media rule').toBeDefined();
    expect(compact!.toString()).toContain('.tv-browse-title { display: none; }');
    expect(compact!.toString()).toContain('width: min(288px, calc(50vw - 1.5rem), calc(100vh - 7rem))');
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

  // The application-shell scroll contract. These are structural invariants
  // rather than looks: the browser tests prove the behaviour, and these prove the
  // shape that produced it cannot quietly be reverted to a scrolling document
  // with two elements pinned over it.
  describe('the shell owns the viewport', () => {
    const root = postcss.parse(CSS, { from: CSS_PATH });

    /** Declarations of the BASE rule for a selector (not a @media override). */
    function baseRule(selector: string): Map<string, string> {
      let found: postcss.Rule | undefined;
      root.walkRules((rule) => {
        if (rule.selector === selector && rule.parent?.type === 'root') found = rule;
      });
      expect(found, `${selector} base rule`).toBeDefined();
      const decls = new Map<string, string>();
      found!.walkDecls((d) => { decls.set(d.prop, d.value); });
      return decls;
    }

    it('constrains its own height instead of growing the document', () => {
      const shell = baseRule('.app-shell');
      // A dynamic-viewport height with a plain-vh fallback ahead of it, so an
      // engine without dvh still gets a bounded shell.
      expect(shell.get('height')).toBe('100dvh');
      expect(String(root)).toContain('height: 100vh;   /* fallback where dvh is unsupported */');
      // min-height would let content push the shell past the viewport again.
      expect(shell.has('min-height')).toBe(false);
    });

    it('makes .app-main the scroll viewport', () => {
      const main = baseRule('.app-main');
      expect(main.get('overflow-y')).toBe('auto');
      expect(main.get('overflow-x')).toBe('hidden');
      // Without this a flex child grows to its content and nothing scrolls.
      expect(main.get('min-height')).toBe('0');
      // No layout jump the moment a page becomes scrollable.
      expect(main.get('scrollbar-gutter')).toBe('stable');
    });

    it('keeps the top gutter off the scroll viewport, so `top: 0` means the top', () => {
      // A padding-block-start here would scroll while a sticky region pinned to
      // the content box stayed put, leaving a gutter-tall strip of moving media
      // above the workspace chrome. The gutter belongs to the content.
      const main = baseRule('.app-main');
      expect(main.get('padding-block')).toBe('0 var(--app-main-gutter)');
      expect(main.has('padding-block-start')).toBe(false);
      expect(main.has('padding-top')).toBe(false);
      let firstChild: postcss.Rule | undefined;
      root.walkRules('.app-main > :first-child', (rule) => { firstChild = rule; });
      expect(firstChild, '.app-main > :first-child').toBeDefined();
      const decls = new Map<string, string>();
      firstChild!.walkDecls((d) => { decls.set(d.prop, d.value); });
      expect(decls.get('margin-block-start')).toBe('var(--app-main-gutter)');
    });

    it('never hands the whole document to the shell by hiding body overflow', () => {
      // Login, TV pairing and the public party pages are not in the shell and
      // still use the document; a global lock would break them.
      root.walkRules((rule) => {
        if (!/(^|,\s*)body(\s*,|$)/.test(rule.selector)) return;
        rule.walkDecls('overflow', (d) => {
          expect(d.value, `body { overflow: ${d.value} }`).not.toBe('hidden');
        });
        rule.walkDecls('overflow-y', (d) => {
          expect(d.value, `body { overflow-y: ${d.value} }`).not.toBe('hidden');
        });
      });
    });

    it('leaves the sidebar knowing nothing about the top bar height', () => {
      // The coupling this replaces was `top: 3.4rem` plus
      // `max-height: calc(100vh - 3.4rem)`: the top bar's height written into the
      // sidebar twice, so changing the bar meant editing rules that are not the
      // bar. The sidebar now fills the body row it is given.
      const sidebar = baseRule('.app-sidebar');
      expect(sidebar.has('top')).toBe(false);
      expect(sidebar.has('max-height')).toBe(false);
      expect(sidebar.get('position')).toBeUndefined();
      // It still scrolls itself on a screen too short for the navigation.
      expect(sidebar.get('overflow-y')).toBe('auto');
    });

    it('sticks the workspace chrome at the top of that viewport, with no measured offset', () => {
      let sticky: postcss.Rule | undefined;
      root.walkRules('.ws-sticky-chrome', (rule) => {
        if (rule.nodes.some((n) => n.type === 'decl' && n.prop === 'position')) sticky = rule;
      });
      expect(sticky, '.ws-sticky-chrome sticky rule').toBeDefined();
      const decls = new Map<string, string>();
      sticky!.walkDecls((d) => { decls.set(d.prop, d.value); });
      expect(decls.get('position')).toBe('sticky');
      // `.app-main` already starts below the global top bar, so the offset is 0 —
      // never a copy of the bar's height.
      expect(decls.get('top')).toBe('0');
      // Desktop only, and off the SAME breakpoint the sidebar/drawer switch uses
      // (max-width: 900px) rather than a second invented one.
      expect((sticky!.parent as postcss.AtRule).params).toBe('(min-width: 901px)');
      expect(CSS).toContain('@media (max-width: 900px)');
    });

    it('adds no transform to the shell or its scroll viewport', () => {
      // A transform on an ancestor makes it the containing block for
      // position: fixed, which would trap the viewer, drawer, sheets and the
      // selection bar inside the scrolling region.
      for (const selector of ['.app-shell', '.app-shell__body', '.app-main']) {
        expect(baseRule(selector).has('transform'), selector).toBe(false);
      }
    });
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

describe('the face viewer bar stays on one line', () => {
  // The reported defect: the decision buttons wrapped, the bar grew a second
  // row and the photograph lost the space. These are the three rules that
  // together keep it to one row at a desktop width.
  const rule = (selector: string): string => {
    const at = CSS.indexOf(`${selector} {`);
    expect(at, `${selector} is declared`).toBeGreaterThan(-1);
    return CSS.slice(at, CSS.indexOf('}', at));
  };

  it('never wraps either group', () => {
    expect(rule('.face-viewer-tools')).toContain('flex-wrap: nowrap');
    expect(rule('.face-viewer-decisions')).toContain('flex-wrap: nowrap');
  });

  it('never breaks a button label across two lines', () => {
    expect(rule('.face-viewer-tool')).toContain('white-space: nowrap');
    expect(CSS).toContain('.face-viewer-secondary,\n.face-viewer-tertiary {\n  white-space: nowrap;');
    expect(rule('.face-viewer-decisions .assign-menu-trigger')).toContain('white-space: nowrap');
  });

  it('sheds the viewport labels before the row can overflow', () => {
    // Text goes before the row does, and the LEFT half's text goes first.
    expect(CSS).toContain('@media (max-width: 75rem)');
    const collapsed = CSS.slice(CSS.indexOf('@media (max-width: 75rem)'));
    expect(collapsed.slice(0, 200)).toContain('.face-viewer-tool-label { display: none; }');
  });
});
