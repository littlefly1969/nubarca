import { describe, expect, it } from 'vitest';
import { contributionModeFrom, withContributionMode } from './partyContributionMode';

describe('party contribution mode', () => {
  it('reads the mode a link asks for, and defaults to media', () => {
    expect(contributionModeFrom('message')).toBe('message');
    expect(contributionModeFrom('media')).toBe('media');
    // Absent, misspelt, or from a version that knows a mode this one does not:
    // all resolve to the page's default rather than to nothing.
    expect(contributionModeFrom(null)).toBe('media');
    expect(contributionModeFrom(undefined)).toBe('media');
    expect(contributionModeFrom('')).toBe('media');
    expect(contributionModeFrom('Message')).toBe('media');
    expect(contributionModeFrom('song')).toBe('media');
  });

  it('sets the mode on the URL the backend gave, keeping the rest of it', () => {
    expect(withContributionMode('/party/up-tok/upload', 'message'))
      .toBe('/party/up-tok/upload?mode=message');
    // A relative path stays relative — the origin is never invented.
    expect(withContributionMode('/party/up-tok/upload', 'media'))
      .toBe('/party/up-tok/upload?mode=media');
    // Whatever else the URL carries survives.
    expect(withContributionMode('/party/up-tok/upload?ref=tv', 'message'))
      .toBe('/party/up-tok/upload?ref=tv&mode=message');
    expect(withContributionMode('/party/up-tok/upload#top', 'message'))
      .toBe('/party/up-tok/upload?mode=message#top');
    // An existing mode is replaced, not duplicated.
    expect(withContributionMode('/party/up-tok/upload?mode=media', 'message'))
      .toBe('/party/up-tok/upload?mode=message');
    // An absolute URL stays absolute.
    expect(withContributionMode('https://party.example/party/up-tok/upload', 'message'))
      .toBe('https://party.example/party/up-tok/upload?mode=message');
  });

  it('hands back anything it cannot parse, rather than mangling it', () => {
    expect(withContributionMode('', 'message')).toBe('');
  });
});
