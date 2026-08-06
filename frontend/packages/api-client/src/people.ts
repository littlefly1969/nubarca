import { api } from './client';

// Owner-private People/Face DTOs. Only logical file ids, names, normalized face
// boxes, and rounded scores — never raw vectors, blob ids, SHA, or paths.
export interface FaceBox {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface FaceRef {
  faceId: string;
  fileItemId: string;
  name: string;
  box: FaceBox;
}

export interface Person {
  personId: string;
  name: string | null;
  faceCount: number;
  representative: FaceRef | null;
}

export interface SuggestedGroup {
  groupId: string;
  representative: FaceRef | null;
  faceCount: number;
  confidence: number | null;
  status: string;
}

export interface PersonPhotoFace {
  faceId: string;
  box: FaceBox;
}

export interface PersonPhoto {
  fileItemId: string;
  name: string;
  faces: PersonPhotoFace[];
}

export interface SimilarFace {
  faceId: string;
  fileItemId: string;
  name: string;
  box: FaceBox;
  score: number;
}

export interface SimilarFacesPage {
  profileAvailable: boolean;
  threshold: number;
  items: SimilarFace[];
  nextCursor: string | null;
  hasMore: boolean;
  unavailableReason: string | null;
}

export function listPeople(signal?: AbortSignal): Promise<Person[]> {
  return api<Person[]>('/api/people', { signal });
}

export function createPerson(name: string | null): Promise<Person> {
  return api<Person>('/api/people', { method: 'POST', json: { name } });
}

export function getPerson(personId: string, signal?: AbortSignal): Promise<Person> {
  return api<Person>(`/api/people/${encodeURIComponent(personId)}`, { signal });
}

export function renamePerson(personId: string, name: string): Promise<Person> {
  return api<Person>(`/api/people/${encodeURIComponent(personId)}`, { method: 'PUT', json: { name } });
}

export function archivePerson(personId: string): Promise<void> {
  return api<void>(`/api/people/${encodeURIComponent(personId)}`, { method: 'DELETE' });
}

export function listSuggestedGroups(review = false, signal?: AbortSignal): Promise<SuggestedGroup[]> {
  const qs = review ? '?review=true' : '';
  return api<SuggestedGroup[]>(`/api/people/suggested-groups${qs}`, { signal });
}

export function assignGroup(
  groupId: string,
  body: { name?: string | null; personId?: string | null },
): Promise<Person> {
  return api<Person>(`/api/people/groups/${encodeURIComponent(groupId)}/assign`, {
    method: 'POST',
    json: body,
  });
}

// Bulk-ignore a suggested/review group: every surfaceable member face is ignored
// individually (lands in "Ignorati", restorable one by one) — the group then leaves
// the suggested queue. Returns how many faces were newly ignored.
export function ignoreGroup(groupId: string): Promise<{ ignored: number }> {
  return api<{ ignored: number }>(`/api/people/groups/${encodeURIComponent(groupId)}/ignore`, {
    method: 'POST',
  });
}

// All surfaceable faces of a suggested/review group (representative first), so the
// whole group can be reviewed face-by-face in the context viewer.
export function getGroupFaces(groupId: string, signal?: AbortSignal): Promise<FaceRef[]> {
  return api<FaceRef[]>(`/api/people/groups/${encodeURIComponent(groupId)}/faces`, { signal });
}

export function addFaceToPerson(personId: string, faceId: string): Promise<void> {
  return api<void>(`/api/people/${encodeURIComponent(personId)}/faces`, {
    method: 'POST',
    json: { faceId },
  });
}

export function removeFaceFromPerson(personId: string, faceId: string): Promise<void> {
  return api<void>(
    `/api/people/${encodeURIComponent(personId)}/faces/${encodeURIComponent(faceId)}`,
    { method: 'DELETE' },
  );
}

export function getPersonPhotos(personId: string, signal?: AbortSignal): Promise<PersonPhoto[]> {
  return api<PersonPhoto[]>(`/api/people/${encodeURIComponent(personId)}/photos`, { signal });
}

// Server-side high-quality face crop (owner-authenticated via cookie). Used as an
// <img src>. Falls back client-side to a thumbnail CSS crop if this 404s.
export function facePreviewUrl(faceId: string, size: 'small' | 'medium' | 'large' = 'small'): string {
  return `/api/people/faces/${encodeURIComponent(faceId)}/preview?size=${size}`;
}

export interface FaceContext {
  fileItemId: string;
  fileName: string;
  selectedFaceId: string;
  selectedBox: FaceBox;
  faces: { faceId: string; box: FaceBox }[];
  personId: string | null;
  personName: string | null;
}

export function getFaceContext(faceId: string, signal?: AbortSignal): Promise<FaceContext> {
  return api<FaceContext>(`/api/people/faces/${encodeURIComponent(faceId)}/context`, { signal });
}

// Assign ONE face to a person: pass `personId` for an existing person or `name`
// to create a new one. Honors one-person-per-face (moves the face if needed).
// Returns the target person (with refreshed count).
export function assignFace(
  faceId: string,
  body: { personId?: string | null; name?: string | null },
): Promise<Person> {
  return api<Person>(`/api/people/faces/${encodeURIComponent(faceId)}/assign`, {
    method: 'POST',
    json: body,
  });
}

// Remove a face's person assignment (whichever person it was on). The face
// becomes unassigned and clusterable again.
export function removeFaceAssignment(faceId: string): Promise<void> {
  return api<void>(`/api/people/faces/${encodeURIComponent(faceId)}/assignment`, { method: 'DELETE' });
}

// Dismiss a single face so it stops surfacing in suggestions / unassigned.
export function ignoreFace(faceId: string): Promise<void> {
  return api<void>(`/api/people/faces/${encodeURIComponent(faceId)}/ignore`, { method: 'POST' });
}

export function unignoreFace(faceId: string): Promise<void> {
  return api<void>(`/api/people/faces/${encodeURIComponent(faceId)}/ignore`, { method: 'DELETE' });
}

export interface ClusterAssignSummary {
  assignedCount: number;
  skippedAlreadyAssignedCount: number;
  skippedIgnoredCount: number;
  skippedIneligibleCount: number;
  clusterStatus: string;
}

// Associate an entire cluster with a person. `dryRun` returns the same counts
// without persisting (for the confirm dialog). By default faces already on another
// person are skipped; `moveAssigned` reassigns them too.
export function assignClusterToPerson(
  personId: string,
  clusterId: string,
  opts: { moveAssigned?: boolean; dryRun?: boolean } = {},
): Promise<ClusterAssignSummary> {
  return api<ClusterAssignSummary>(
    `/api/people/${encodeURIComponent(personId)}/clusters/${encodeURIComponent(clusterId)}/assign`,
    { method: 'POST', json: { moveAssigned: opts.moveAssigned ?? false, dryRun: opts.dryRun ?? false } },
  );
}

export interface UnassignedFace {
  faceId: string;
  fileItemId: string;
  name: string;
  box: FaceBox;
  hasEmbedding: boolean;
  detectionScore: number | null;
}

export interface UnassignedFacesPage {
  items: UnassignedFace[];
  nextCursor: string | null;
  profileAvailable: boolean;
}

export interface IgnoredFace {
  faceId: string;
  fileItemId: string;
  name: string;
  box: FaceBox;
}

export interface IgnoredFacesPage {
  items: IgnoredFace[];
  nextCursor: string | null;
}

export function getIgnoredFaces(
  opts: { limit?: number; cursor?: string | null } = {},
  signal?: AbortSignal,
): Promise<IgnoredFacesPage> {
  const params = new URLSearchParams();
  if (opts.limit !== undefined) params.set('limit', String(opts.limit));
  if (opts.cursor) params.set('cursor', opts.cursor);
  const qs = params.toString();
  return api<IgnoredFacesPage>(`/api/people/ignored-faces${qs ? `?${qs}` : ''}`, { signal });
}

export function getUnassignedFaces(
  opts: { limit?: number; cursor?: string | null; hasEmbedding?: boolean; sort?: string } = {},
  signal?: AbortSignal,
): Promise<UnassignedFacesPage> {
  const params = new URLSearchParams();
  if (opts.limit !== undefined) params.set('limit', String(opts.limit));
  if (opts.cursor) params.set('cursor', opts.cursor);
  if (opts.hasEmbedding !== undefined) params.set('hasEmbedding', String(opts.hasEmbedding));
  if (opts.sort) params.set('sort', opts.sort);
  const qs = params.toString();
  return api<UnassignedFacesPage>(`/api/people/unassigned-faces${qs ? `?${qs}` : ''}`, { signal });
}

export function getPersonSimilarFaces(
  personId: string,
  minSimilarity: number,
  limit?: number,
  cursor?: string | null,
  signal?: AbortSignal,
): Promise<SimilarFacesPage> {
  const params = new URLSearchParams();
  params.set('minSimilarity', String(minSimilarity));
  if (limit !== undefined) params.set('limit', String(limit));
  if (cursor !== undefined && cursor !== null) params.set('cursor', cursor);
  return api<SimilarFacesPage>(
    `/api/people/${encodeURIComponent(personId)}/similar-faces?${params.toString()}`,
    { signal },
  );
}

// ---- VFACE-02: owner-level identity over canonical VIDEO face tracks -------
//
// A track is blob-level EVIDENCE; the decision about who it shows is the
// owner's. These DTOs carry logical file ids, millisecond intervals and person
// names only. The `trackId` appears solely on the authenticated review surface,
// because the client needs it to post a decision back.

// One temporal match inside one video. Shaped like the VSEM-03 semantic match so
// the same player handoff applies; `evidenceType` is 'person' here.
export interface PersonVideoMatch {
  evidenceType: string;
  startMilliseconds: number;
  endMilliseconds: number;
  representativeMilliseconds: number;
}

export interface PersonVideo {
  fileItemId: string;
  name: string;
  bestMatch: PersonVideoMatch;
  additionalMatches: PersonVideoMatch[];
}

// One undecided track awaiting review.
export interface VideoFaceTrackReview {
  trackId: string;
  fileItemId: string;
  name: string;
  startMilliseconds: number;
  endMilliseconds: number;
  representativeMilliseconds: number;
  detectionCount: number;
  qualityScore: number;
}

export interface VideoFaceTrackReviewPage {
  items: VideoFaceTrackReview[];
  hasMore: boolean;
}

// A candidate person for a track. `similarity` is a cosine similarity, NOT a
// probability — the UI must not present it as a confidence percentage of
// "being" someone.
export interface VideoFaceTrackSuggestion {
  personId: string;
  name: string | null;
  similarity: number;
  supportingEvidenceCount: number;
}

export interface VideoFaceTrackSuggestions {
  threshold: number;
  items: VideoFaceTrackSuggestion[];
  unavailableReason: string | null;
}

// Videos where this person has a CONFIRMED track. Undecided and ignored tracks
// never appear.
export function getPersonVideos(personId: string, signal?: AbortSignal): Promise<PersonVideo[]> {
  return api<PersonVideo[]>(`/api/people/${encodeURIComponent(personId)}/videos`, { signal });
}

// Videos where both people have confirmed tracks that OVERLAP in time.
export function getPersonCoPresentVideos(
  personId: string,
  otherPersonId: string,
  signal?: AbortSignal,
): Promise<PersonVideo[]> {
  return api<PersonVideo[]>(
    `/api/people/${encodeURIComponent(personId)}/co-present/${encodeURIComponent(otherPersonId)}`,
    { signal },
  );
}

export function listUndecidedVideoFaceTracks(
  limit?: number,
  signal?: AbortSignal,
): Promise<VideoFaceTrackReviewPage> {
  const qs = limit === undefined ? '' : `?limit=${limit}`;
  return api<VideoFaceTrackReviewPage>(`/api/people/video-tracks/undecided${qs}`, { signal });
}

// Advisory only: asking never changes a decision.
export function getVideoFaceTrackSuggestions(
  trackId: string,
  limit?: number,
  signal?: AbortSignal,
): Promise<VideoFaceTrackSuggestions> {
  const qs = limit === undefined ? '' : `?limit=${limit}`;
  return api<VideoFaceTrackSuggestions>(
    `/api/people/video-tracks/${encodeURIComponent(trackId)}/suggestions${qs}`,
    { signal },
  );
}

// Confirm an EXISTING person of the caller's. There is deliberately no "create a
// person from this track" call: naming people stays in the People flows.
export function assignVideoFaceTrack(trackId: string, personId: string): Promise<void> {
  return api<void>(`/api/people/video-tracks/${encodeURIComponent(trackId)}/assign`, {
    method: 'POST',
    json: { personId },
  });
}

export function ignoreVideoFaceTrack(trackId: string): Promise<void> {
  return api<void>(`/api/people/video-tracks/${encodeURIComponent(trackId)}/ignore`, {
    method: 'POST',
  });
}

// Back to undecided. Nothing is reassigned automatically.
export function clearVideoFaceTrackDecision(trackId: string): Promise<void> {
  return api<void>(`/api/people/video-tracks/${encodeURIComponent(trackId)}/decision`, {
    method: 'DELETE',
  });
}
