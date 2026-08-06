import { useState } from 'react';
import { OrganizeByDateWizard } from '../components/files/OrganizeByDateWizard';
import { useI18n } from '../i18n';

// Launcher for the existing DateTaken organizer wizard.
//
// The wizard is a modal with its own multi-step flow, so the tool panel is just
// the in-page explanation plus the button that opens it — the same behaviour the
// old "Organize by date" card had, now rendered inside the hub instead of
// alongside three unrelated cards.
export function OrganizeByDatePanel() {
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  const [banner, setBanner] = useState<{ tone: 'info' | 'error'; text: string } | null>(null);

  return (
    <section className="cloud-tool-body">
      <p className="muted">{t('cloud.organizeHint')}</p>

      {banner && (
        <div
          className={banner.tone === 'error' ? 'folder-error' : 'folder-banner'}
          role={banner.tone === 'error' ? 'alert' : 'status'}
          data-testid="cf-organize-banner"
        >
          {banner.text}
        </div>
      )}

      <button
        type="button"
        className="row-action-primary"
        data-testid="cf-organize"
        onClick={() => setOpen(true)}
      >
        {t('cloud.organizeBtn')}
      </button>

      {open && (
        <OrganizeByDateWizard
          currentFolderId={null}
          currentFolderName={t('cloud.allPhotos')}
          selectedFileIds={[]}
          onClose={() => setOpen(false)}
          onDone={(message) => {
            setBanner(message);
            setOpen(false);
          }}
        />
      )}
    </section>
  );
}
