import { useI18n } from '../i18n';

// The guest's persistent navigation, once they have left the cover behind.
//
// It carries NAVIGATION, not a second copy of the capability deck: where am I,
// where else can I go, and the one action a party is for. Dedications, songs
// and printing will never belong here — a dock that grows a card per feature is
// the menu this experience is built to avoid.
//
// It renders nothing while hidden rather than fading out in place: a control
// nobody can see must not be reachable by Tab either, and `opacity: 0` leaves it
// in the tab order.

export type GuestSection = 'home' | 'album';

function HomeIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M4 10.5 12 4l8 6.5" />
      <path d="M6 9.8V19a1 1 0 0 0 1 1h10a1 1 0 0 0 1-1V9.8" />
      <path d="M10 20v-5h4v5" />
    </svg>
  );
}

function GridIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <rect x="4" y="4" width="7" height="7" rx="1.8" />
      <rect x="13" y="4" width="7" height="7" rx="1.8" />
      <rect x="4" y="13" width="7" height="7" rx="1.8" />
      <rect x="13" y="13" width="7" height="7" rx="1.8" />
    </svg>
  );
}

function CameraPlusIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M3.5 8.75A2.25 2.25 0 0 1 5.75 6.5h1.6l1.1-1.85A1.5 1.5 0 0 1 9.74 4h4.52a1.5 1.5 0 0 1 1.29.73l1.1 1.87h1.6a2.25 2.25 0 0 1 2.25 2.25v8A2.25 2.25 0 0 1 18.25 19H5.75a2.25 2.25 0 0 1-2.25-2.25Z" />
      <path d="M12 9.6v5.8M9.1 12.5h5.8" />
    </svg>
  );
}

export function PartyGuestDock({
  visible,
  section,
  contributionUrl,
  onHome,
  onAlbum,
}: {
  /** False until the guest has scrolled past the cover — see PartyPage. */
  visible: boolean;
  section: GuestSection;
  /** The one real signal for this action; null means the party takes no uploads. */
  contributionUrl: string | null;
  onHome: () => void;
  onAlbum: () => void;
}) {
  const { t } = useI18n();
  if (!visible) return null;

  return (
    <nav
      className="party-guest-dock"
      aria-label={t('partyDock.label')}
      data-testid="party-dock"
      data-share={contributionUrl ? 'yes' : 'no'}
    >
      <div className="party-guest-dock-inner">
        <button
          type="button"
          className="party-guest-dock-item"
          data-testid="party-dock-home"
          aria-current={section === 'home' ? 'true' : undefined}
          onClick={onHome}
        >
          <HomeIcon />
          <span>{t('partyDock.home')}</span>
        </button>
        <button
          type="button"
          className="party-guest-dock-item"
          data-testid="party-dock-album"
          aria-current={section === 'album' ? 'true' : undefined}
          onClick={onAlbum}
        >
          <GridIcon />
          <span>{t('partyDock.album')}</span>
        </button>
        {/* The dominant action when the party accepts contributions, and simply
            absent when it does not — the two remaining items then share the
            dock rather than leaving a hole where this was. */}
        {contributionUrl && (
          <a
            className="party-guest-dock-share"
            data-testid="party-dock-share"
            href={contributionUrl}
          >
            <CameraPlusIcon />
            <span>{t('partyDock.share')}</span>
          </a>
        )}
      </div>
    </nav>
  );
}
