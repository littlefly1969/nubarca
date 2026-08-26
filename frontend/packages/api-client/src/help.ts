import { api } from './client';

/// Where the model that answers Help questions sits, relative to this
/// installation.
///
/// `external` — the question leaves NubArca for a third-party provider.
/// `localTrusted` — it is processed by an endpoint the operator declares as
/// their own. NubArca cannot prove the second one has no internet egress, and
/// the copy is written so it does not claim to.
export type HelpModelBoundary = 'external' | 'localTrusted';

/// What the browser is allowed to know about the Help model. Deliberately not
/// the base URL, the model id or any header: a user needs to know whether the
/// feature exists, which service is involved, and whether their words leave the
/// installation. Everything else is operator configuration.
export interface HelpAssistantStatus {
  enabled: boolean;
  providerLabel: string;
  knowledgeAvailable: boolean;
  modelBoundary: HelpModelBoundary;
}

export interface HelpChatTurn {
  fromUser: boolean;
  text: string;
}

export type HelpChatAnswer =
  | { ok: true; text: string; sources: string[] }
  | { ok: false; reason: string };

export function getHelpAssistantStatus(signal?: AbortSignal): Promise<HelpAssistantStatus> {
  return api<HelpAssistantStatus>('/api/help/ai/status', { signal });
}

/**
 * Ask the Help assistant.
 *
 * The request carries a QUESTION and a short conversation, and nothing else.
 * There is no field here for a file, an album, a person, a search or the media
 * currently on screen — not because the server would ignore them, but because
 * the shape has nowhere to put them. Attaching library context would be a change
 * to this signature, which is exactly where a reviewer would see it.
 *
 * There is no field for a model or a boundary either. Which model answers, and
 * what it may be given, is operator configuration read server-side; a browser
 * that asked for a different one would simply be ignored.
 */
export function askHelpAssistant(
  message: string,
  history: HelpChatTurn[],
  signal?: AbortSignal,
): Promise<HelpChatAnswer> {
  return api<HelpChatAnswer>('/api/help/ai/chat', {
    method: 'POST',
    json: { message, history },
    signal,
  });
}
