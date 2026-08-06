import { api } from './client';

// Admin-only Face AI settings (People v0). Booleans/keys/numbers only — no model
// paths, raw vectors, or storage identifiers.
export interface FaceThresholds {
  clusterSimilarityThreshold: number;
  candidateSimilarityThreshold: number;
  searchDefaultSimilarityThreshold: number;
  searchMinSimilarity: number;
  searchMaxSimilarity: number;
  maxFacesPerImage: number;
  // Louvain resolution γ for the pgvector_knn+Louvain path (admin-editable).
  knnLouvainResolution: number;
}

export interface FaceModelPresence {
  profileKey: string;
  dimension: number;
  detectorPresent: boolean;
  recognitionPresent: boolean;
}

// Read-only view of the active clustering strategy. Controlled via server config
// (Ai:Face:*); shown for visibility, not edited here.
export interface FaceClusteringInfo {
  mode: string;
  knnNeighbors: number;
  knnEfSearch: number;
  knnMinSimilarity: number;
  knnCandidateSimilarity: number;
  knnMaxEligibleFacesPerRun: number;
  knnMaxClusterSize: number;
  exactMaxFacesToCluster: number;
}

export interface FaceDiagnostics {
  aiEnabled: boolean;
  faceDetectionEnabled: boolean;
  faceEmbeddingsEnabled: boolean;
  faceClusteringEnabled: boolean;
  activeFaceProfileKey: string | null;
  modelDirConfigured: boolean;
  onnxIntraOpThreads: number | null;
  maxConcurrency: number;
  thresholds: FaceThresholds;
  models: FaceModelPresence[];
  clustering: FaceClusteringInfo;
}

export type FaceThresholdsUpdate = Partial<FaceThresholds>;

export function getFaceSettings(signal?: AbortSignal): Promise<FaceDiagnostics> {
  return api<FaceDiagnostics>('/api/admin/ai/face-settings', { signal });
}

export function updateFaceSettings(update: FaceThresholdsUpdate): Promise<FaceDiagnostics> {
  return api<FaceDiagnostics>('/api/admin/ai/face-settings', { method: 'PUT', json: update });
}

// NOTE: the former bounded face-job triggers (runFaceJob) were consolidated
// into the unified admin jobs console — see enqueueAdminJob in adminJobs.ts
// and the ai-faces-* commands. Face settings (thresholds) stay here.
