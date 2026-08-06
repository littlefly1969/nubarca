import { Navigate, useSearchParams } from 'react-router';

// Legacy → unified route redirect. Maps the old photo/video gallery routes onto
// the unified workspace, carrying the compatible query parameters through:
//   /gallery          → /media?kind=image
//   /videos           → /media?kind=video
//   /gallery/excluded → /media/excluded?kind=image
//   /videos/excluded  → /media/excluded?kind=video
// The old query params (q, sort, direction, albumMembership, similarTo,
// includePeople/…) share names with the unified contract, so they are preserved
// verbatim; only `kind` is added and the path encodes the scope.

interface Props {
  kind: 'image' | 'video';
  scope: 'active' | 'excluded';
}

export function LegacyMediaRedirect({ kind, scope }: Props) {
  const [searchParams] = useSearchParams();
  const next = new URLSearchParams(searchParams);
  next.set('kind', kind);
  next.delete('scope'); // scope is encoded in the path, not the query
  const path = scope === 'excluded' ? '/media/excluded' : '/media';
  const qs = next.toString();
  return <Navigate to={qs ? `${path}?${qs}` : path} replace />;
}
