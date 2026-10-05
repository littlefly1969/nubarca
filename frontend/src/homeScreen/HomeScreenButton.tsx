import { useState } from 'react';
import { Modal } from '../components/Overlay';
import { useI18n, type MessageKey } from '../i18n';
import {
  homeScreenPlatform, isStandalone, promptInstall, useHomeScreenState, type HomeScreenPlatform,
} from './homeScreen';
import '../party/PartyRsvp.css';
import './HomeScreenButton.css';

// "INSTALLA": a party, or an album shared by link, kept on the visitor's home
// screen, one tap from inside. ONE button for both — only the words differ, and
// which app it installs is the page's manifest, never the button's.
//
// Where the browser offers to install the page (Android's Chrome, a desktop
// Chrome) the tap is the browser's own dialog. An iPhone has no such dialog,
// and an Android browser may not have offered one: there the tap explains the
// two steps the browser's own menu takes. Opened from the home screen already,
// or just installed, there is nothing to offer and nothing is drawn.

export type HomeScreenSubject = 'party' | 'album';

interface Copy {
  button: MessageKey;
  buttonLabel: MessageKey;
  title: MessageKey;
  lead: MessageKey;
  done: MessageKey;
  steps: Record<Exclude<HomeScreenPlatform, 'other'>, MessageKey[]>;
}

const COPY: Record<HomeScreenSubject, Copy> = {
  party: {
    button: 'partyHome.button',
    buttonLabel: 'partyHome.buttonLabel',
    title: 'partyHome.title',
    lead: 'partyHome.lead',
    done: 'partyHome.done',
    steps: {
      ios: ['partyHome.ios.step1', 'partyHome.ios.step2', 'partyHome.ios.step3'],
      android: ['partyHome.android.step1', 'partyHome.android.step2', 'partyHome.android.step3'],
    },
  },
  album: {
    button: 'albumHome.button',
    buttonLabel: 'albumHome.buttonLabel',
    title: 'albumHome.title',
    lead: 'albumHome.lead',
    done: 'partyHome.done',
    steps: {
      ios: ['partyHome.ios.step1', 'partyHome.ios.step2', 'albumHome.ios.step3'],
      android: ['partyHome.android.step1', 'partyHome.android.step2', 'albumHome.android.step3'],
    },
  },
};

export function HomeScreenButton({ subject }: { subject: HomeScreenSubject }) {
  const { t } = useI18n();
  const { canPrompt, installed } = useHomeScreenState();
  const [explaining, setExplaining] = useState(false);
  const platform = homeScreenPlatform();
  const copy = COPY[subject];

  if (installed || isStandalone()) return null;
  // A computer is offered only what its browser offers by itself.
  if (platform === 'other' && !canPrompt) return null;

  const open = async () => {
    if (canPrompt && await promptInstall()) return;
    setExplaining(true);
  };

  return (
    <>
      <button
        type="button"
        className="party-home-button"
        data-testid={`${subject}-home-button`}
        aria-label={t(copy.buttonLabel)}
        onClick={() => void open()}
      >
        <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
          <rect x="6.5" y="2.5" width="11" height="19" rx="2.5" />
          <path d="M12 8.5v6M9 11.5h6" />
        </svg>
        <span>{t(copy.button)}</span>
      </button>
      {explaining && platform !== 'other' && (
        <Modal
          className="party-rsvp-sheet party-home-sheet"
          title={t(copy.title)}
          onClose={() => setExplaining(false)}
          testId={`${subject}-home-sheet`}
          footer={(
            <div className="party-rsvp-foot">
              <button type="button" className="party-rsvp-submit" onClick={() => setExplaining(false)}>
                {t(copy.done)}
              </button>
            </div>
          )}
        >
          <p className="party-home-lead">{t(copy.lead)}</p>
          <ol className="party-home-steps" data-testid={`${subject}-home-steps-${platform}`}>
            {copy.steps[platform].map((step) => <li key={step}>{t(step)}</li>)}
          </ol>
        </Modal>
      )}
    </>
  );
}
