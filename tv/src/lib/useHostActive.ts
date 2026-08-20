import { useEffect, useState } from 'react';
import { AppState, type AppStateStatus } from 'react-native';

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
export function useHostActive(): boolean {
  const [active, setActive] = useState(() => AppState.currentState === 'active');
  useEffect(() => {
    const subscription = AppState.addEventListener('change', (next: AppStateStatus) => {
      setActive(next === 'active');
    });
    return () => subscription.remove();
  }, []);
  return active;
}
