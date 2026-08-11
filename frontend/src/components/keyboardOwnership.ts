// Who owns a keystroke when surfaces are stacked.
//
// Several viewers (the media viewer, the face context viewer, the vault viewer)
// register their shortcuts on `window`, because the photo must respond to arrows
// wherever the focus happens to be. That is right until something opens ON TOP of
// them. A `window` listener keeps firing for a modal's own keystrokes, and
// `stopPropagation` does not stop a SIBLING listener on the same target — so
// pressing ArrowLeft to move the caret inside a modal's search field also paged
// the photo underneath, and Escape closed the modal AND the viewer behind it.
//
// The rule these helpers express:
//
//     THE TOPMOST MODAL OWNS THE KEYBOARD.
//
// A viewer asks two questions before acting on a key:
//
//   1. did this come from somewhere the user is TYPING? → arrows are caret moves,
//      not navigation;
//   2. did this come from a dialog that is not MINE? → the key is not mine at all,
//      not even Escape.
//
// Neither helper ever calls preventDefault: the browser's own behaviour inside an
// input (caret, selection, Home/End, IME) must stay exactly as it is. This is
// about which JS handler responds, never about what the control does.

const EDITABLE_TAGS = new Set(['INPUT', 'TEXTAREA', 'SELECT']);

// A target the user is typing or selecting into: form controls and anything
// inside a contenteditable region (so a caret in a nested <b> counts too).
export function isEditableKeyboardTarget(target: EventTarget | null): boolean {
  if (!(target instanceof Element)) return false;
  if (EDITABLE_TAGS.has(target.tagName)) return true;
  // `closest` rather than the element's own flag: the caret can sit in a
  // descendant of the contenteditable host, which is not itself editable.
  return target.closest('[contenteditable]:not([contenteditable="false"])') !== null;
}

// True when `root` is the modal surface the event belongs to — i.e. nothing else
// modal is between the event's target and this viewer.
//
// The comparison is against the viewer's OWN root, not merely "is there an
// aria-modal around this": the viewer is itself an aria-modal, so a bare
// existence check would make it ignore its own keys. A target with no modal
// ancestor at all (focus on <body> after a click on the backdrop) belongs to the
// viewer as well, which is what keeps plain arrow navigation working.
export function ownsKeyboardEvent(root: Element | null, target: EventTarget | null): boolean {
  if (root === null) return true; // not mounted yet — nothing to defer to
  if (!(target instanceof Element)) return true;
  const owner = target.closest('[role="dialog"][aria-modal="true"]');
  return owner === null || owner === root;
}

// The keys a topmost modal consumes so surfaces underneath cannot also act on
// them. Deliberately narrow: Tab stays free for focus traps, and printable keys
// are never touched.
const OWNED_KEYS = new Set(['Escape', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End']);

export function isModalOwnedKey(key: string): boolean {
  return OWNED_KEYS.has(key);
}
