import { api } from './client';

/// What the browser is allowed to know about the external Help provider.
/// Deliberately not the base URL, the model or any header: a user needs to know
/// whether the feature exists and which provider is involved, and everything
/// else is operator configuration.
export interface ExternalHelpStatus {
  enabled: boolean;
  providerLabel: string;
  knowledgeAvailable: boolean;
}

export interface HelpChatTurn {
  fromUser: boolean;
  text: string;
}

export type HelpChatAnswer =
  | { ok: true; text: string; sources: string[] }
  | { ok: false; reason: string };

export function getExternalHelpStatus(signal?: AbortSignal): Promise<ExternalHelpStatus> {
  return api<ExternalHelpStatus>('/api/help/ai/status', { signal });
}

/**
 * Ask the external Help assistant.
 *
 * The request carries a QUESTION and a short conversation, and nothing else.
 * There is no field here for a file, an album, a person, a search or the media
 * currently on screen — not because the server would ignore them, but because
 * the shape has nowhere to put them. Attaching library context would be a change
 * to this signature, which is exactly where a reviewer would see it.
 */
export function askExternalHelp(
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
