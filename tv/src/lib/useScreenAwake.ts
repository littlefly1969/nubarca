import { useEffect, useId, useRef } from 'react';
import { activateKeepAwakeAsync, deactivateKeepAwake } from 'expo-keep-awake';
import { KeepAwakeController, type KeepAwakeDriver } from './keepAwake';

// Bridges the pure KeepAwakeController to Expo's official keep-awake module.
// `expo-keep-awake` ships inside the `expo` meta-package and its native
// KeepAwakeModule is already autolinked into the current runtime, so this is a
// JS-only capability — no new APK / OTA runtime increment is required to use it.
//
// Native calls are fire-and-forget: a rejected activate/deactivate (e.g. the
// screen was already torn down) must never crash the viewer, and the controller
// keeps the JS-side held/released bookkeeping authoritative regardless.
const nativeDriver: KeepAwakeDriver = {
  activate: (tag) => {
    void activateKeepAwakeAsync(tag).catch(() => { /* best effort */ });
  },
  deactivate: (tag) => {
    void deactivateKeepAwake(tag).catch(() => { /* best effort */ });
  },
};

// Keep the screen awake while `active` is true and the owning component is
// mounted; release it the moment `active` goes false, and ALWAYS on unmount.
//
// `active` should reflect whether a slideshow/viewer is genuinely visible — the
// caller passes `true` for a live viewer and `false` for anything that is not
// one (a grid, a filter panel, a metadata sheet). The unique per-instance tag
// (useId) means two viewers can never deactivate each other's lock.
export function useScreenAwake(active: boolean, driver: KeepAwakeDriver = nativeDriver): void {
  const tag = useId();
  const controllerRef = useRef<KeepAwakeController | null>(null);
  if (controllerRef.current === null) {
    controllerRef.current = new KeepAwakeController(driver, `nubarca-viewer-${tag}`);
  }

  useEffect(() => {
    controllerRef.current!.sync(active);
  }, [active]);

  useEffect(() => {
    // Release on unmount — the app's conditional-render navigation unmounts the
    // viewer on exit, Personal Area lock, session invalidation and pairing
    // revocation, so this single cleanup covers every teardown path.
    const controller = controllerRef.current!;
    return () => controller.release();
  }, []);
}
