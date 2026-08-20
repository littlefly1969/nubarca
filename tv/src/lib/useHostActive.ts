import { useEffect, useState } from 'react';
import { AppState, type AppStateStatus } from 'react-native';
import { hostStateFromAppState, type HostState } from '../video/playerLifecycle';

// Is the application genuinely in the foreground?
//
// One hook, because three separate things need the same answer and must not
// disagree about it: the photo keep-awake lock, the slideshow rotation timer,
// and the video player's release policy. A slideshow that keeps advancing
// photographs behind HOME is doing work nobody can see AND dragging a wake lock
// along with it.
//
// 'inactive' counts as NOT active for this purpose. It is a brief interruption
// — an overlay, a system dialog — and during it there is still nobody watching,
// so nothing should be rotating or holding the screen on. That is deliberately
// a different judgement from the video player's RELEASE policy, which treats
// only a real 'background' as a teardown (see video/playerLifecycle.ts):
// stopping a timer is free, re-preparing ExoPlayer is not.
// react-native-tvos maps Android's Activity callbacks onto AppState:
// onHostPause() → 'background', onHostResume() → 'active', and both fire before
// onStop. That is why this architecture needs no native lifecycle bridge — the
// standard signal is early enough to release the player in time.
export function useHostState(): HostState {
  const [host, setHost] = useState<HostState>(
    () => hostStateFromAppState(AppState.currentState));
  useEffect(() => {
    const subscription = AppState.addEventListener('change', (next: AppStateStatus) => {
      setHost(hostStateFromAppState(next));
    });
    return () => subscription.remove();
  }, []);
  return host;
}

/**
 * The coarse view: is the app in the foreground?
 *
 * 'inactive' counts as NOT active. Stopping a timer or releasing a wake lock
 * for a brief interruption is free, so the conservative answer is the right one
 * for those. Changing what the USER asked for is a different matter — that
 * follows the finer 'background' signal, because a momentary overlay must not
 * silently turn their slideshow off.
 */
export function useHostActive(): boolean {
  return useHostState() === 'active';
}
