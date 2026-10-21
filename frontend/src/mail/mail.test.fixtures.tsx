// Test-only fixtures and harness for the MailArchive suite. Shapes follow
// docs/mailarchive/contract.md §5 exactly.
import { render } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { AuthedWrapper } from '../test-utils';
import type { AuthContextValue } from '../auth/AuthContext';
import { MailPage } from './MailPage';
import type {
  MailArchive, MailFolder, MailMessageDetail, MailMessageSummary, MailRun,
} from './types';

export function run(overrides: Partial<MailRun> = {}): MailRun {
  return {
    id: 'run-1',
    archiveId: 'arc-1',
    status: 'running',
    phase: 'importing',
    totalMessages: 100,
    processedMessages: 40,
    importedMessages: 30,
    alreadyPresentMessages: 8,
    skippedDeletedMessages: 1,
    failedMessages: 1,
    attachmentsCreated: 12,
    attachmentsReused: 3,
    attachmentsSkipped: 2,
    cancellationRequested: false,
    errorCode: null,
    errorMessage: null,
    createdAt: '2026-10-01T10:00:00Z',
    startedAt: '2026-10-01T10:00:01Z',
    completedAt: null,
    scanErrors: 0,
    scanIssues: [],
    ...overrides,
  };
}

export function archive(overrides: Partial<MailArchive> = {}): MailArchive {
  return {
    id: 'arc-1',
    displayName: 'Ufficio 2019',
    format: 'unicode',
    sourceAvailable: true,
    sourceFileName: 'ufficio.pst',
    messageCount: 1234,
    folderCount: 7,
    createdAt: '2026-10-01T09:00:00Z',
    lastImportedAt: '2026-10-01T11:00:00Z',
    latestRun: run({ status: 'succeeded', phase: null, processedMessages: 100, completedAt: '2026-10-01T11:00:00Z' }),
    ...overrides,
  };
}

export const FOLDERS: MailFolder[] = [
  { id: 'f-root', parentId: null, name: 'Top of Outlook data file', depth: 0, messageCount: 0 },
  { id: 'f-inbox', parentId: 'f-root', name: 'Posta in arrivo', depth: 1, messageCount: 3 },
  { id: 'f-clients', parentId: 'f-inbox', name: 'Clienti', depth: 2, messageCount: 2 },
  { id: 'f-sent', parentId: 'f-root', name: 'Posta inviata', depth: 1, messageCount: 1 },
];

export function summary(id: string, overrides: Partial<MailMessageSummary> = {}): MailMessageSummary {
  return {
    id,
    archiveId: 'arc-1',
    folderId: 'f-inbox',
    subject: `Oggetto ${id}`,
    from: { name: 'Ada Example', address: 'ada@example.test' },
    toSummary: 'Bob',
    date: '2024-03-01T10:00:00Z',
    hasAttachments: false,
    attachmentCount: 0,
    preview: `Anteprima ${id}`,
    ...overrides,
  };
}

export function detail(id: string, overrides: Partial<MailMessageDetail> = {}): MailMessageDetail {
  return {
    ...summary(id, { hasAttachments: true, attachmentCount: 3 }),
    subject: 'Relazione trimestrale',
    archiveName: 'Ufficio 2019',
    folderPath: ['Top of Outlook data file', 'Posta in arrivo'],
    to: [{ name: 'Bob', address: 'bob@example.test' }],
    cc: [{ name: null, address: 'carla@example.test' }],
    bcc: [],
    sentAt: '2024-03-01T10:00:00Z',
    receivedAt: '2024-03-01T10:00:05Z',
    internetMessageId: '<abc@example.test>',
    messageClass: 'IPM.Note',
    body: { kind: 'html', html: '<p>Ciao <b>Bob</b></p>', text: 'Ciao Bob', truncated: false },
    attachments: [
      {
        id: 'att-1', name: 'report.pdf', mimeType: 'application/pdf', sizeBytes: 20480, inline: false,
        contentId: null, embeddedPath: null, state: 'available', skipReason: null,
        fileItemId: 'file-9', currentFileName: 'report.pdf',
      },
      {
        id: 'att-2', name: 'foto.jpg', mimeType: 'image/jpeg', sizeBytes: 1000, inline: false,
        contentId: null, embeddedPath: null, state: 'file_deleted', skipReason: null,
        fileItemId: null, currentFileName: null,
      },
      {
        id: 'att-3', name: 'enorme.iso', mimeType: null, sizeBytes: null, inline: false,
        contentId: null, embeddedPath: null, state: 'not_extracted', skipReason: 'too_large',
        fileItemId: null, currentFileName: null,
      },
    ],
    embeddedMessages: [
      { path: '1', subject: 'Fwd: ciao', from: { name: 'Carla', address: null }, date: '2024-02-01T09:00:00Z', bodyText: 'Testo inoltrato' },
    ],
    ...overrides,
  };
}

function LocationProbe() {
  const loc = useLocation();
  return <output data-testid="location">{`${loc.pathname}${loc.search}`}</output>;
}

export function renderMail(path: string, auth?: Partial<AuthContextValue>) {
  return render(
    <AuthedWrapper value={auth}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/mail" element={<MailPage />} />
          <Route path="/mail/messages/:messageId" element={<MailPage />} />
          <Route path="/" element={<p>files</p>} />
        </Routes>
        <LocationProbe />
      </MemoryRouter>
    </AuthedWrapper>,
  );
}

export function currentLocation(): string {
  return document.querySelector('[data-testid="location"]')?.textContent ?? '';
}
