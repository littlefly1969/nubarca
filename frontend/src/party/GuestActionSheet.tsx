import { Modal } from '../components/Overlay';

// THE MENU, as a surface a thumb can hit.
//
// A row of five buttons on a card is unusable on a phone and unreachable
// without a pointer on a desktop, so a card carries ONE primary action and a
// menu. The menu is the shared overlay — portalled, focus-trapped, Escape and
// backdrop close it — laid out as a list of full-width targets that slides up
// from the bottom edge on a narrow screen and sits as a small dialog on a wide
// one. Nothing here depends on hover.

export interface GuestAction {
  key: string;
  label: string;
  onSelect(): void;
  danger?: boolean;
  testId?: string;
  disabled?: boolean;
}

export function GuestActionSheet({
  title, actions, onClose, testId,
}: {
  title: string;
  actions: GuestAction[];
  onClose(): void;
  testId?: string;
}) {
  return (
    <Modal title={title} onClose={onClose} testId={testId} className="guest-sheet" focusPanelOnOpen>
      <ul className="guest-action-list">
        {actions.map((action) => (
          <li key={action.key}>
            <button
              type="button"
              className={`guest-action${action.danger ? ' guest-action--danger' : ''}`}
              data-testid={action.testId}
              disabled={action.disabled}
              onClick={action.onSelect}
            >
              {action.label}
            </button>
          </li>
        ))}
      </ul>
    </Modal>
  );
}
