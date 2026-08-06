import { createContext } from 'react';
import {
  DEFAULT_THEME_PREFERENCE,
  type EffectiveTheme,
  type ThemePreference,
} from './themePreference';

export interface ThemeContextValue {
  // What the user chose (may be 'system').
  preference: ThemePreference;
  // What is actually painted right now ('system' already resolved).
  effective: EffectiveTheme;
  setPreference(next: ThemePreference): void;
}

// The default value keeps `useTheme` usable outside a provider (public pages,
// isolated component tests) without crashing: it reports the product default
// and treats a change as a no-op.
export const ThemeContext = createContext<ThemeContextValue>({
  preference: DEFAULT_THEME_PREFERENCE,
  effective: 'dark',
  setPreference: () => {},
});
