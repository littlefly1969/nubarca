// MailArchive DTOs, exactly as docs/mailarchive/contract.md §5 freezes them.
//
// Owner-private and display-safe by construction: no storage key, hash,
// fingerprint, physical path, PST NID or raw document is ever part of these
// shapes, so nothing here can render one.

export type MailStatusReason = 'ok' | 'disabled' | 'importer_missing';

export interface MailStatus {
  available: boolean;
  reason: MailStatusReason;
}

export interface MailSourceCandidate {
  fileItemId: string;
  name: string;
  sizeBytes: number;
  createdAt: string;
  alreadyImportedArchiveId: string | null;
}

export type MailRunStatus = 'queued' | 'running' | 'succeeded' | 'partial' | 'failed' | 'cancelled';
export type MailRunPhase = 'preparing' | 'scanning' | 'importing' | 'finalizing';

export interface MailRun {
  id: string;
  archiveId: string;
  status: MailRunStatus;
  phase: MailRunPhase | null;
  totalMessages: number;
  processedMessages: number;
  importedMessages: number;
  alreadyPresentMessages: number;
  skippedDeletedMessages: number;
  failedMessages: number;
  attachmentsCreated: number;
  attachmentsReused: number;
  attachmentsSkipped: number;
  cancellationRequested: boolean;
  errorCode: string | null;
  errorMessage: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  /** Folders or messages the scan could not enumerate; > 0 makes the run partial and the next run scans again. */
  scanErrors: number;
  scanIssues: MailScanIssue[];
}

/** One unreadable folder or message found by the scan (path by name; null when the engine could not place it). */
export interface MailScanIssue {
  scope: 'folder' | 'message';
  code: string;
  folderPath: string[] | null;
}

export interface MailArchive {
  id: string;
  displayName: string;
  format: 'unicode' | 'ansi' | null;
  sourceAvailable: boolean;
  sourceFileName: string | null;
  messageCount: number;
  folderCount: number;
  createdAt: string;
  lastImportedAt: string | null;
  latestRun: MailRun | null;
}

export interface MailFolder {
  id: string;
  parentId: string | null;
  name: string;
  depth: number;
  messageCount: number;
}

export interface MailAddress {
  name: string | null;
  address: string | null;
}

export interface MailMessageSummary {
  id: string;
  archiveId: string;
  folderId: string;
  subject: string | null;
  from: MailAddress | null;
  toSummary: string | null;
  date: string | null;
  hasAttachments: boolean;
  attachmentCount: number;
  preview: string | null;
}

export interface MailMessagePage {
  items: MailMessageSummary[];
  nextCursor: string | null;
  // The quick search also reads the message text, but some messages have no
  // searchable text yet (imported before it existed; being added).
  bodySearchIncomplete?: boolean;
}

export type MailAttachmentState = 'available' | 'file_deleted' | 'not_extracted';

export interface MailAttachmentView {
  id: string;
  name: string;
  mimeType: string | null;
  sizeBytes: number | null;
  inline: boolean;
  contentId: string | null;
  embeddedPath: string | null;
  state: MailAttachmentState;
  skipReason: string | null;
  fileItemId: string | null;
  currentFileName: string | null;
}

export interface MailEmbeddedMessage {
  path: string;
  subject: string | null;
  from: MailAddress | null;
  date: string | null;
  bodyText: string | null;
}

export type MailBodyKind = 'html' | 'text' | 'rtf' | 'none';

export interface MailMessageDetail extends MailMessageSummary {
  archiveName: string;
  folderPath: string[];
  to: MailAddress[];
  cc: MailAddress[];
  bcc: MailAddress[];
  sentAt: string | null;
  receivedAt: string | null;
  internetMessageId: string | null;
  messageClass: string | null;
  body: { kind: MailBodyKind; html: string | null; text: string | null; truncated: boolean };
  attachments: MailAttachmentView[];
  embeddedMessages: MailEmbeddedMessage[];
}

export interface MailFileOrigin {
  messageId: string;
  archiveId: string;
  archiveName: string;
  subject: string | null;
  from: MailAddress | null;
  date: string | null;
  attachmentName: string;
}

export interface MailRunFailure {
  folderPath: string[];
  position: number;
  code: string;
  message: string | null;
}

export interface MailRunFailurePage {
  items: MailRunFailure[];
  nextCursor: string | null;
}

export const TERMINAL_RUN_STATUSES: readonly MailRunStatus[] = ['succeeded', 'partial', 'failed', 'cancelled'];

export function isRunActive(run: MailRun | null | undefined): boolean {
  return run != null && (run.status === 'queued' || run.status === 'running');
}
