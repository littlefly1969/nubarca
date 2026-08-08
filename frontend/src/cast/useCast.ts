import { useContext } from 'react';
import { CastContext, type CastContextValue } from './CastContext';

/**
 * The Cast session, or null outside the authenticated shell.
 *
 * Null rather than throwing: the public surfaces (login, party links, the
 * browser TV page) render plenty of the same components and legitimately have
 * no Cast provider above them. A component asks, and hides its Cast affordance
 * when the answer is null.
 */
export function useCast(): CastContextValue | null {
  return useContext(CastContext);
}
