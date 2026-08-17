import { tvGet } from './client';
import { personalHeaders } from './personal';

// The owner's people, for the library filter's person picker.
//
// This is the SAME grant-gated, owner-scoped TV projection the retired photo
// gallery used (`/api/tv/personal/gallery/people`, resolved through
// ResolveTvPersonalAccessAsync like every other personal call). The endpoint
// survived the move to the unified library; only this client module and the
// picker above it were lost with the old gallery, which is why the people
// filter became unreachable from a remote.
//
// The projection is deliberately narrow and stays that way: a person id, a
// display name and a face COUNT. No representative face reference, no bounding
// boxes, no crops, no embeddings, no blob or storage identity — nothing the
// television needs, and nothing an unlocked living-room screen should carry.
// The limited TV session is NOT authorized on the owner-web People endpoints;
// it reaches people only through this projection.
export interface TvPersonalPerson {
  id: string;
  name: string | null;
  faceCount: number;
}

// Called lazily, only when the picker opens — the filter panel itself never
// pays for it, and a user who never filters by person never fetches the list.
export function listPersonalPeople(): Promise<TvPersonalPerson[]> {
  return tvGet<TvPersonalPerson[]>('/api/tv/personal/gallery/people', personalHeaders());
}
