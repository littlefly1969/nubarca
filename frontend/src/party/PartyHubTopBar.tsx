import { LanguageSwitcher } from '../components/LanguageSwitcher';
import { PRODUCT_NAME } from '../brand/brand';

// The guest hub is a FIXED dark surface — a party cover, not a themed app page —
// so the approved ON-DARK wordmark is pinned here instead of resolved from the
// visitor's theme (which is what <BrandMark> does, and would put the Midnight
// Navy artwork on a Midnight Navy hero). Byte-exact approved asset, rendered at
// its own proportions, unfiltered and unrecoloured; CSS only sets its width.
const PARTY_WORDMARK = {
  src: '/brand/nubarca-wordmark-on-dark-480w.png',
  width: 480,
  height: 135,
} as const;

// Wordmark + language switcher: the same top row on the hero and on the
// unavailable/error states, so a guest always knows where they are — on the
// party's own page and on a personal invitation alike.
export function PartyHubTopBar() {
  return (
    <div className="party-guest-hub-topbar">
      <img
        className="party-guest-hub-logo"
        src={PARTY_WORDMARK.src}
        alt={PRODUCT_NAME}
        width={PARTY_WORDMARK.width}
        height={PARTY_WORDMARK.height}
      />
      <LanguageSwitcher className="language-switcher language-switcher-public" compact />
    </div>
  );
}
