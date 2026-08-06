// 10-foot UI constants: large type, generous spacing, high-contrast focus.
export const colors = {
  bg: '#080b10',
  panel: '#141b26',
  panelFocused: '#1f2c3f',
  accent: '#0a84ff',
  text: '#f8fafc',
  muted: '#a7b0bd',
  danger: '#ff6b6b',
};

export const spacing = {
  xs: 8,
  sm: 12,
  md: 20,
  lg: 32,
  xl: 48,
};

export const font = {
  title: 44,
  heading: 32,
  body: 22,
  button: 20,
  caption: 18,
  code: 56,
};

// Focus ring width used across focusable tiles/buttons.
export const focusRing = 4;

// TV overscan safe-area insets (~3.5% per edge, floored). Overlay chrome (QR
// corners, title, command bars) is positioned inside these so it is never
// clipped on 1080p or 720p panels.
export function overscan(width: number, height: number): { x: number; y: number } {
  return {
    x: Math.max(24, Math.round(width * 0.035)),
    y: Math.max(16, Math.round(height * 0.035)),
  };
}

// Responsive party-QR edge for the MENU overlays: ~13% of the window height
// (≈70dp on the usual 960×540dp TV layout → ~140 physical px at 1080p, and
// proportionally smaller on 720p panels). Phones scan this fine up close and it
// no longer dominates the picture.
export function overlayQrSize(height: number): number {
  return Math.min(140, Math.max(64, Math.round(height * 0.13)));
}
