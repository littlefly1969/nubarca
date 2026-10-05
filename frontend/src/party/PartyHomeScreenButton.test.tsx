import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, renderHook, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { I18nProvider } from '../i18n';
import { PartyHomeScreenButton } from './PartyHomeScreenButton';
import { homeScreenPlatform, usePartyHomeScreen } from './partyHomeScreen';

// "INSTALLA": the party kept on a guest's home screen. The browser's own
// dialog where it offers one, two steps explained where it does not, nothing
// at all where the party is already opened from the home screen.

const IPHONE = 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1';
const IPAD_AS_MAC = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 Version/18.0 Safari/605.1.15';
const ANDROID = 'Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 Chrome/141.0 Mobile Safari/537.36';
const DESKTOP = 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/141.0 Safari/537.36';

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  document.head.innerHTML = '';
  document.title = '';
});

function as(userAgent: string) {
  vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue(userAgent);
}

function button() {
  return render(<I18nProvider><PartyHomeScreenButton /></I18nProvider>);
}

describe('homeScreenPlatform', () => {
  it('knows an iPhone, an iPad that says it is a Mac, Android and a computer', () => {
    expect(homeScreenPlatform(IPHONE, 5)).toBe('ios');
    expect(homeScreenPlatform(IPAD_AS_MAC, 5)).toBe('ios');
    expect(homeScreenPlatform(IPAD_AS_MAC, 0)).toBe('other');
    expect(homeScreenPlatform(ANDROID, 5)).toBe('android');
    expect(homeScreenPlatform(DESKTOP, 0)).toBe('other');
  });
});

describe('usePartyHomeScreen', () => {
  it('names the page and the home-screen app after the party, and gives the product its title back after', () => {
    document.head.innerHTML = '<meta name="apple-mobile-web-app-title" content="NubArca">';
    document.title = 'NubArca';

    const { unmount } = renderHook(() => usePartyHomeScreen('Matrimonio di Marta'));

    expect(document.head.querySelector('meta[name="apple-mobile-web-app-title"]')).toHaveAttribute('content', 'Matrimonio di Marta');
    expect(document.title).toBe('Matrimonio di Marta');
    unmount();
    expect(document.head.querySelector('meta[name="apple-mobile-web-app-title"]')).toHaveAttribute('content', 'NubArca');
    expect(document.title).toBe('NubArca');
  });

  it('touches nothing before the party has loaded', () => {
    document.title = 'NubArca';
    renderHook(() => usePartyHomeScreen(null));
    expect(document.title).toBe('NubArca');
  });
});

describe('PartyHomeScreenButton', () => {
  it('is not offered on a computer whose browser offers nothing', () => {
    as(DESKTOP);
    button();
    expect(screen.queryByTestId('party-home-button')).not.toBeInTheDocument();
  });

  it('is not offered when the party is already opened from the home screen', () => {
    as(IPHONE);
    vi.stubGlobal('matchMedia', (query: string) => ({ matches: query === '(display-mode: standalone)' }));
    button();
    expect(screen.queryByTestId('party-home-button')).not.toBeInTheDocument();
  });

  it('on an iPhone explains Share, then Add to Home Screen', async () => {
    as(IPHONE);
    button();

    const install = screen.getByTestId('party-home-button');
    expect(install).toHaveTextContent('Installa');
    expect(install).toHaveAccessibleName('Aggiungi la festa alla schermata Home');
    await userEvent.click(install);

    const steps = await screen.findByTestId('party-home-steps-ios');
    expect(steps).toHaveTextContent('Condividi');
    expect(steps).toHaveTextContent('Aggiungi alla schermata Home');
    await userEvent.click(screen.getByRole('button', { name: 'Ho capito' }));
    expect(screen.queryByTestId('party-home-sheet')).not.toBeInTheDocument();
  });

  it('on Android with no offer from the browser, explains its menu', async () => {
    as(ANDROID);
    button();

    await userEvent.click(screen.getByTestId('party-home-button'));

    expect(await screen.findByTestId('party-home-steps-android')).toHaveTextContent('Installa app');
  });

  // Last: an accepted install is remembered for the rest of the visit.
  it('on Android, where the browser offers it, is the browser’s own dialog — and then gone', async () => {
    as(ANDROID);
    const prompt = vi.fn(async () => {});
    button();
    act(() => {
      window.dispatchEvent(Object.assign(new Event('beforeinstallprompt', { cancelable: true }), {
        prompt, userChoice: Promise.resolve({ outcome: 'accepted' }),
      }));
    });

    await userEvent.click(screen.getByTestId('party-home-button'));

    expect(prompt).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId('party-home-sheet')).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-home-button')).not.toBeInTheDocument();
  });
});
