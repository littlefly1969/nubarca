import { useEffect, useState } from 'react';

// WHERE THE CONSOLE SPLITS IN TWO.
//
// From this width the guest console works as master/detail: the list stays on
// the left while a group is open beside it. Below it, a group opens as a
// full-screen sheet over the list — two columns on a phone would be two
// unusable ones. The width is measured in rem so it follows the reader's own
// font size, and it is deliberately generous: the authenticated shell spends
// part of the viewport on its sidebar.
//
// Without matchMedia (jsdom, a very old browser) the answer is "narrow", which
// is the layout that works everywhere.

export const WIDE_LAYOUT_QUERY = '(min-width: 64rem)';

function matches(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return false;
  try {
    return window.matchMedia(WIDE_LAYOUT_QUERY).matches;
  } catch {
    return false;
  }
}

export function useWideLayout(): boolean {
  const [wide, setWide] = useState(matches);

  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return;
    let query: MediaQueryList;
    try {
      query = window.matchMedia(WIDE_LAYOUT_QUERY);
    } catch {
      return;
    }
    const onChange = () => setWide(query.matches);
    onChange();
    query.addEventListener?.('change', onChange);
    return () => query.removeEventListener?.('change', onChange);
  }, []);

  return wide;
}
