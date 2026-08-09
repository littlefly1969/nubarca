import { useCallback, useEffect, useRef } from 'react';
import { TV_CODE_LENGTH, TV_CODE_SYMBOLS, type TvCodeSymbol } from '@nubarca/api-client';
import { useI18n } from '../i18n';

// Capture control for the TV Personal Area DIRECTIONAL code.
//
// The code is entered on a television with the remote, blind — the TV screen
// never shows which symbol was pressed, because a person sitting in the room
// would otherwise read the secret straight off the display. THIS surface is the
// opposite case: an authenticated, owner-private management page on the owner's
// own device, where they are deliberately choosing and confirming a secret they
// must be able to remember. Showing it here is the point; hiding it would make
// the code unconfigurable. The two behaviours are intentional opposites and
// each is documented where it lives.
//
// Entry works with the arrow keys + Enter/Space (the same muscle memory as the
// remote) and with the on-screen buttons for pointer/touch. Backspace removes
// the last symbol.

const SYMBOL_GLYPH: Record<TvCodeSymbol, string> = {
  U: '↑',
  D: '↓',
  L: '←',
  R: '→',
  S: '●',
};

// Keyboard → symbol. Only these keys produce a symbol; anything else falls
// through to the browser, so the form stays operable (Tab, Escape, Enter on the
// submit button once the code is complete).
const KEY_SYMBOL: Record<string, TvCodeSymbol> = {
  ArrowUp: 'U',
  ArrowDown: 'D',
  ArrowLeft: 'L',
  ArrowRight: 'R',
  Enter: 'S',
  ' ': 'S',
};

interface Props {
  label: string;
  value: string;
  onChange: (next: string) => void;
  disabled?: boolean;
  // Set on the confirmation field so the two inputs are distinguishable to
  // assistive technology and to tests.
  id: string;
}

export function TvCodeInput({ label, value, onChange, disabled = false, id }: Props) {
  const { t } = useI18n();
  const valueRef = useRef(value);
  valueRef.current = value;

  const append = useCallback(
    (symbol: TvCodeSymbol) => {
      if (disabled) return;
      const current = valueRef.current;
      if (current.length >= TV_CODE_LENGTH) return;
      onChange(current + symbol);
    },
    [disabled, onChange],
  );

  const removeLast = useCallback(() => {
    if (disabled) return;
    onChange(valueRef.current.slice(0, -1));
  }, [disabled, onChange]);

  const onKeyDown = useCallback(
    (event: React.KeyboardEvent<HTMLDivElement>) => {
      if (event.key === 'Backspace') {
        event.preventDefault();
        removeLast();
        return;
      }
      const symbol = KEY_SYMBOL[event.key];
      if (symbol === undefined) return;
      // Arrow keys would otherwise scroll the page and Space would activate a
      // focused control — neither is what the user means inside this field.
      event.preventDefault();
      append(symbol);
    },
    [append, removeLast],
  );

  const complete = value.length === TV_CODE_LENGTH;

  return (
    <div className="tv-code-input">
      <span className="tv-code-input__label" id={`${id}-label`}>
        {label}
      </span>
      <div
        id={id}
        className="tv-code-input__field"
        role="group"
        aria-labelledby={`${id}-label`}
        aria-describedby={`${id}-hint`}
        tabIndex={disabled ? -1 : 0}
        onKeyDown={onKeyDown}
      >
        {Array.from({ length: TV_CODE_LENGTH }, (_, i) => (
          <span
            key={i}
            className={`tv-code-input__slot${i < value.length ? ' is-filled' : ''}`}
            aria-hidden="true"
          >
            {i < value.length ? SYMBOL_GLYPH[value[i] as TvCodeSymbol] : '○'}
          </span>
        ))}
      </div>
      <p className="tv-code-input__hint muted" id={`${id}-hint`}>
        {t('tvCode.entryHint', { count: String(value.length), total: String(TV_CODE_LENGTH) })}
      </p>
      <div className="tv-code-input__pad">
        {TV_CODE_SYMBOLS.map((symbol) => (
          <button
            key={symbol}
            type="button"
            className="tv-code-input__key"
            disabled={disabled || complete}
            aria-label={t(`tvCode.symbol.${symbol}`)}
            onClick={() => append(symbol)}
          >
            {SYMBOL_GLYPH[symbol]}
          </button>
        ))}
        <button
          type="button"
          className="tv-code-input__key tv-code-input__key--erase"
          disabled={disabled || value.length === 0}
          onClick={removeLast}
        >
          {t('tvCode.delete')}
        </button>
      </div>
    </div>
  );
}

// Focus-on-mount helper for the first field of a setup form, so a keyboard user
// can start entering immediately without hunting for the control.
export function useAutoFocus<T extends HTMLElement>(active: boolean) {
  const ref = useRef<T | null>(null);
  useEffect(() => {
    if (active) ref.current?.focus();
  }, [active]);
  return ref;
}
