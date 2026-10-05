import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { I18nProvider } from '../i18n';
import { PartyGuestDock } from './PartyGuestDock';

afterEach(cleanup);

function dockAt(basename: string | undefined, entry: string) {
  render(
    <I18nProvider>
      <MemoryRouter basename={basename} initialEntries={[entry]}>
        <PartyGuestDock
          visible
          section="home"
          phase="live"
          contributionUrl="/party/upload-token/upload"
          onHome={() => {}}
          onAlbum={() => {}}
        />
      </MemoryRouter>
    </I18nProvider>,
  );
  return screen.getByTestId('party-dock-share');
}

describe('the dock\'s way to contribute', () => {
  it('is the contribution page itself on the party\'s own link', () => {
    expect(dockAt(undefined, '/party/tok-1')).toHaveAttribute('href', '/party/upload-token/upload');
  });

  it('stays inside the party\'s app when the page is one', () => {
    const app = '/party/app/0123456789abcdef0123456789abcdef';
    expect(dockAt(app, `${app}/party/tok-1`)).toHaveAttribute('href', `${app}/party/upload-token/upload`);
  });
});
