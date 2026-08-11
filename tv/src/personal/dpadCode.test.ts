import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import {
  DPAD_CODE_LENGTH,
  DPAD_CODE_SPACE,
  DPAD_SYMBOLS,
  dpadCodeReducer,
  dpadSymbolForEvent,
  dpadSymbolForKey,
  EMPTY_DPAD_ENTRY,
  isComplete,
  type DpadCodeEntry,
} from './dpadCode.ts';

function enter(keys: string[]): DpadCodeEntry {
  return keys.reduce((state, key) => {
    const symbol = dpadSymbolForKey(key);
    return symbol === null ? state : dpadCodeReducer(state, { type: 'SYMBOL', symbol });
  }, EMPTY_DPAD_ENTRY);
}

test('the alphabet is exactly the five blind-findable remote buttons', () => {
  assert.deepEqual([...DPAD_SYMBOLS], ['U', 'D', 'L', 'R', 'S']);
});

test('entropy is at least that of the 6-digit PIN it replaces', () => {
  assert.equal(DPAD_CODE_LENGTH, 9);
  assert.equal(DPAD_CODE_SPACE, 1_953_125);
  assert.ok(DPAD_CODE_SPACE > 1_000_000, 'must beat the 10^6 numeric-PIN space');
});

test('only the five secret buttons produce a symbol', () => {
  assert.equal(dpadSymbolForKey('up'), 'U');
  assert.equal(dpadSymbolForKey('down'), 'D');
  assert.equal(dpadSymbolForKey('left'), 'L');
  assert.equal(dpadSymbolForKey('right'), 'R');
  assert.equal(dpadSymbolForKey('select'), 'S');
  // The centre button is reported as playPause by some remotes; a user must not
  // have to know which kind they hold.
  assert.equal(dpadSymbolForKey('playPause'), 'S');
});

test('navigation and system keys are never secret symbols', () => {
  for (const key of ['menu', 'back', 'home', 'rewind', 'fastForward', 'stop', 'pause', 'info']) {
    assert.equal(dpadSymbolForKey(key), null, `${key} must not be a secret symbol`);
  }
});

test('each accepted press appends exactly one symbol', () => {
  const state = enter(['up', 'right', 'select']);
  assert.equal(state.code, 'URS');
  assert.equal(state.code.length, 3);
});

test('Android TV key-up produces one symbol and optional key-down is ignored', () => {
  assert.equal(dpadSymbolForEvent('up', 0), null, 'ACTION_DOWN must not duplicate the press');
  assert.equal(dpadSymbolForEvent('up', 1), 'U', 'ACTION_UP is the event Android always emits');
});

test('TV runtimes without eventKeyAction still produce one symbol', () => {
  assert.equal(dpadSymbolForEvent('select', undefined), 'S');
});

test('non-symbol keys leave the code untouched', () => {
  const state = enter(['up', 'menu', 'back', 'right']);
  assert.equal(state.code, 'UR');
});

test('erase removes exactly one symbol, and does nothing when empty', () => {
  const three = enter(['up', 'down', 'left']);
  const two = dpadCodeReducer(three, { type: 'ERASE' });
  assert.equal(two.code, 'UD');
  const empty = dpadCodeReducer(EMPTY_DPAD_ENTRY, { type: 'ERASE' });
  assert.equal(empty, EMPTY_DPAD_ENTRY, 'erase on empty is a no-op (BACK then cancels)');
});

test('the code completes at exactly nine symbols and never grows further', () => {
  const nine = enter(['up', 'up', 'up', 'down', 'down', 'down', 'left', 'left', 'left']);
  assert.equal(nine.code, 'UUUDDDLLL');
  assert.ok(isComplete(nine));
  const overflow = dpadCodeReducer(nine, { type: 'SYMBOL', symbol: 'R' });
  assert.equal(overflow.code, 'UUUDDDLLL', 'a tenth press must not shift the code');
});

test('input is frozen while a submission is in flight', () => {
  const nine = enter(['up', 'up', 'up', 'down', 'down', 'down', 'left', 'left', 'left']);
  const submitting = dpadCodeReducer(nine, { type: 'SUBMITTED' });
  const stray = dpadCodeReducer(submitting, { type: 'SYMBOL', symbol: 'S' });
  assert.equal(stray.code, nine.code, 'a repeat during the request must not start a new code');
  const erase = dpadCodeReducer(submitting, { type: 'ERASE' });
  assert.equal(erase.code, nine.code);
});

test('reset clears both the code and the submitting flag after a failure', () => {
  const nine = enter(['up', 'up', 'up', 'down', 'down', 'down', 'left', 'left', 'left']);
  const after = dpadCodeReducer(dpadCodeReducer(nine, { type: 'SUBMITTED' }), { type: 'RESET' });
  assert.deepEqual(after, EMPTY_DPAD_ENTRY);
});

test('the state exposes only a length, never which symbols were entered', () => {
  // The screen renders `code.length` dots. This asserts the ONLY thing a
  // renderer is given about progress is a count: any future field that leaked a
  // symbol, a last-direction or a per-press marker would fail here.
  const state = enter(['up', 'right']);
  assert.deepEqual(Object.keys(state).sort(), ['code', 'submitting']);
});
