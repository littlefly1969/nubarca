import { vi } from 'vitest';
import type { ReactNode } from 'react';
import { AuthedWrapper } from '../test-utils';
import { UploadProvider, useUploads } from './UploadProvider';
import type { UploadEntry } from './engine/uploadManager';
import type { UploadTarget } from './engine/targets';
import { UploadManager } from './engine/uploadManager';
import { inMemoryUploadMemory } from './engine/uploadMemory';
import { TransportError, type TransportRequest, type TransportResponse } from './engine/transport';

// A real upload engine over a scripted transport, for component tests.

export type TestHandler = (request: TransportRequest) => TransportResponse | Promise<TransportResponse>;

export function respond(body: unknown, status = 201): TransportResponse {
  return { status, body, retryAfterMs: null };
}

export function testEngine(handler: TestHandler = (req) => respond({ id: 'id-1', name: fileName(req) })) {
  const calls: TransportRequest[] = [];
  const transport = vi.fn(async (request: TransportRequest) => {
    calls.push(request);
    if (request.signal?.aborted) throw new TransportError('aborted');
    return handler(request);
  });
  const manager = new UploadManager({
    transport,
    memory: inMemoryUploadMemory(),
    retryBaseDelayMs: 1,
    maxAttempts: 2,
    notifyIntervalMs: 0,
    random: () => 0,
    isOnline: () => true,
  });
  return { manager, calls };
}

export function fileName(request: TransportRequest): string {
  const form = request.body;
  return form instanceof FormData ? (form.get('file') as File).name : '';
}

export function EngineWrapper({ manager, children }: { manager: UploadManager; children: ReactNode }) {
  return (
    <AuthedWrapper>
      <UploadProvider manager={manager}>{children}</UploadProvider>
    </AuthedWrapper>
  );
}

type Enqueue = (entries: readonly UploadEntry[], target: UploadTarget) => void;

/** Renders inside an EngineWrapper; `queue.enqueue` then queues files the way a picker does (opening the tray). */
export function queueHandle(): { enqueue: Enqueue; Probe: () => null } {
  const handle = {
    enqueue: (() => { throw new Error('Probe not rendered'); }) as Enqueue,
    Probe: () => {
      handle.enqueue = useUploads().enqueue;
      return null;
    },
  };
  return handle;
}
