// Pure keep-awake lifecycle controller (no React, no native imports) — covered
// by node --test. It owns the single invariant that matters for screen-sleep
// prevention: at most ONE outstanding activation per controller, always paired
// with a matching deactivation, so a viewer can never leak a keep-awake handle
// or double-activate the same tag.
//
// The React hook (useScreenAwake.ts) drives this with the viewer's "is the
// slideshow actually visible" boolean and calls release() on unmount; every
// teardown path in this app (viewer exit, Personal Area lock, session
// invalidation, pairing revocation) unmounts the viewer, so release-on-unmount
// covers them all.

export interface KeepAwakeDriver {
  // Prevent the screen from sleeping under `tag`. Idempotent per tag on the
  // native side, but the controller guarantees we never call it twice without
  // an intervening deactivate.
  activate(tag: string): void;
  // Release the lock previously taken under `tag`.
  deactivate(tag: string): void;
}

export class KeepAwakeController {
  private held = false;
  private readonly driver: KeepAwakeDriver;
  private readonly tag: string;

  constructor(driver: KeepAwakeDriver, tag: string) {
    this.driver = driver;
    this.tag = tag;
  }

  // Reconcile the desired active state with what we currently hold. Issues at
  // most one driver call and only on a real transition — repeated sync(true)
  // never re-activates (no duplicate tags), repeated sync(false) never emits a
  // spurious deactivate.
  sync(active: boolean): void {
    if (active && !this.held) {
      this.held = true;
      this.driver.activate(this.tag);
    } else if (!active && this.held) {
      this.held = false;
      this.driver.deactivate(this.tag);
    }
  }

  // Unconditional teardown for unmount/lock/revocation. Deactivates only if a
  // lock is actually held, so it is safe to call from any state.
  release(): void {
    this.sync(false);
  }

  get isHeld(): boolean {
    return this.held;
  }
}
