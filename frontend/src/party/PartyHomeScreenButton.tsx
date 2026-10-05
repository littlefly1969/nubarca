import { useState } from 'react';
import { Modal } from '../components/Overlay';
import { useI18n, type MessageKey } from '../i18n';
import {
  homeScreenPlatform, isStandalone, promptInstall, useHomeScreenState, type HomeScreenPlatform,
} from './partyHomeScreen';
import './PartyRsvp.css';

// "INSTALLA": the party kept on the guest's home screen, one tap from inside.
//
// Where the browser offers to install the page (Android's Chrome, a desktop
// Chrome) the tap is the browser's own dialog. An iPhone has no such dialog,
// and an Android browser may not have offered one: there the tap explains the
// two steps the browser's own menu takes. Opened from the home screen already,
// or just installed, there is nothing to offer and nothing is drawn.

const STEPS: Record<Exclude<HomeScreenPlatform, 'other'>, MessageKey[]> = {
  ios: ['partyHome.ios.step1', 'partyHome.ios.step2', 'partyHome.ios.step3'],
  android: ['partyHome.android.step1', 'partyHome.android.step2', 'partyHome.android.step3'],
};

export function PartyHomeScreenButton() {
  const { t } = useI18n();
  const { canPrompt, installed } = useHomeScreenState();
  const [explaining, setExplaining] = useState(false);
  const platform = homeScreenPlatform();

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
        data-testid="party-home-button"
        aria-label={t('partyHome.buttonLabel')}
        onClick={() => void open()}
      >
        <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
          <rect x="6.5" y="2.5" width="11" height="19" rx="2.5" />
          <path d="M12 8.5v6M9 11.5h6" />
        </svg>
        <span>{t('partyHome.button')}</span>
      </button>
      {explaining && platform !== 'other' && (
        <Modal
          className="party-rsvp-sheet party-home-sheet"
          title={t('partyHome.title')}
          onClose={() => setExplaining(false)}
          testId="party-home-sheet"
          footer={(
            <div className="party-rsvp-foot">
              <button type="button" className="party-rsvp-submit" onClick={() => setExplaining(false)}>
                {t('partyHome.done')}
              </button>
            </div>
          )}
        >
          <p className="party-home-lead">{t('partyHome.lead')}</p>
          <ol className="party-home-steps" data-testid={`party-home-steps-${platform}`}>
            {STEPS[platform].map((step) => <li key={step}>{t(step)}</li>)}
          </ol>
        </Modal>
      )}
    </>
  );
}
