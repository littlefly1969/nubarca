import { describe, expect, it } from 'vitest';
import {
  PARTY_MESSAGE_LIMITS,
  isPartyMessageSubmittable,
  normalizePartyMessageText,
  partyDisplayNameRemaining,
  partyMessageLength,
  partyMessageRemaining,
} from '@nubarca/api-client';

// The browser half of the text contract, exercised through the package's public
// entry point (vitest only collects `src/**`, and this is a contract test rather
// than an internal one).
//
// The browser half of the text contract. EVERY case here has a twin in
// `tests/NubArca.Api.Tests/Party/PartyMessageTextTests.cs`, deliberately with
// the same inputs and the same expected numbers: this file is only worth having
// if it fails when the two implementations drift apart.
describe('party message text contract', () => {
  describe('counting', () => {
    it('counts unicode code points, not UTF-16 units', () => {
      expect(partyMessageLength('hello')).toBe(5);

      // A single astral code point is ONE, though `.length` says 2. Counting
      // `.length` would disagree with the server on the first emoji typed.
      expect('🎉'.length).toBe(2);
      expect(partyMessageLength('🎉')).toBe(1);

      // A heart with a variation selector is deliberately TWO.
      expect(partyMessageLength('❤️')).toBe(2);

      // A ZWJ family sequence is SEVEN — four people and three joiners. The
      // grapheme count would be 1; we do not use grapheme counts, on purpose.
      expect(partyMessageLength('👨‍👩‍👧‍👦')).toBe(7);

      // e + COMBINING ACUTE ACCENT, written as escapes so no editor or tool can
      // silently precompose it into the single code point U+00E9 and make this
      // test agree with itself for the wrong reason.
      expect(partyMessageLength('e\u0301')).toBe(2);
      expect(partyMessageLength('\u00e9')).toBe(1);
    });

    it('reports the remaining budget against the NORMALISED text', () => {
      expect(partyMessageRemaining('')).toBe(PARTY_MESSAGE_LIMITS.text);
      expect(partyMessageRemaining('a'.repeat(120))).toBe(0);
      expect(partyMessageRemaining('a'.repeat(121))).toBe(-1);

      // Padding collapses, so the counter stops moving rather than climbing
      // towards a rejection the guest cannot see coming.
      expect(partyMessageRemaining('  ciao  ')).toBe(PARTY_MESSAGE_LIMITS.text - 4);
      expect(partyMessageRemaining('ciao       ')).toBe(PARTY_MESSAGE_LIMITS.text - 4);
    });

    it('agrees with the server on the 120-emoji boundary', () => {
      const full = '🎉'.repeat(120);
      expect(partyMessageLength(full)).toBe(120);
      expect(full.length).toBe(240);
      expect(partyMessageRemaining(full)).toBe(0);
      expect(partyMessageRemaining(full + '🎉')).toBe(-1);
    });

    it('measures a 164-character padded input as exactly 120', () => {
      // The same fixture as the C# test, so the two cannot drift.
      const padded = `   ${Array(40).fill('ab').join('  ')}x\n\n`;
      expect(padded.length).toBe(164);
      expect(partyMessageLength(normalizePartyMessageText(padded))).toBe(120);
      expect(partyMessageRemaining(padded)).toBe(0);
    });
  });

  describe('normalisation', () => {
    it('turns every line ending into one space and collapses runs', () => {
      expect(normalizePartyMessageText('a\r\nb\rc\nd')).toBe('a b c d');
      expect(normalizePartyMessageText('  a \t\t b  ')).toBe('a b');
    });

    it('removes bidi overrides and zero-width padding', () => {
      expect(normalizePartyMessageText('auguri‮gnorw')).toBe('augurignorw');
      // Isolates, zero-width space and soft hyphen: three ways to pad a message
      // past a limit while looking short.
      expect(normalizePartyMessageText('a⁦b⁩c​d­e')).toBe('abcde');
    });

    it('keeps the joiners, because dropping them would corrupt real text', () => {
      expect(normalizePartyMessageText('👨‍👩‍👧‍👦')).toBe('👨‍👩‍👧‍👦');
      expect(normalizePartyMessageText('می‌روم')).toBe('می‌روم');
    });

    it('passes emoji, punctuation and accents through untouched', () => {
      const greeting = 'Serata fantastica! Auguri ragazzi ❤️🎉 — «davvero»';
      expect(normalizePartyMessageText(greeting)).toBe(greeting);
    });

    it('reduces every empty shape to the empty string', () => {
      for (const input of [null, undefined, '', '   ', '\t\n\r\n  ', '  ', '​​']) {
        expect(normalizePartyMessageText(input)).toBe('');
      }
    });
  });

  describe('submittability', () => {
    it('refuses empty, whitespace-only and over-length bodies', () => {
      expect(isPartyMessageSubmittable('', null)).toBe(false);
      expect(isPartyMessageSubmittable('   ', null)).toBe(false);
      expect(isPartyMessageSubmittable('​', null)).toBe(false);
      expect(isPartyMessageSubmittable('a'.repeat(121), null)).toBe(false);
      expect(isPartyMessageSubmittable('a'.repeat(120), null)).toBe(true);
    });

    it('refuses an over-length name even when the body is fine', () => {
      expect(isPartyMessageSubmittable('ciao', 'n'.repeat(40))).toBe(true);
      expect(isPartyMessageSubmittable('ciao', 'n'.repeat(41))).toBe(false);
      expect(partyDisplayNameRemaining('n'.repeat(41))).toBe(-1);
    });

    it('treats a blank name as no name at all', () => {
      expect(isPartyMessageSubmittable('ciao', '   ')).toBe(true);
      expect(partyDisplayNameRemaining('   ')).toBe(PARTY_MESSAGE_LIMITS.displayName);
    });
  });
});
