import { useCallback, useEffect, useRef, useState } from 'react';

/* THE FRONT CAMERA, opened and released.
 *
 * The party's "find my photos" used to hand the whole job to the operating
 * system: `<input type="file" capture="user">` opens the phone's own camera
 * app, and the guest comes back holding a picture. That works, and it costs the
 * product every step in between — there is no moment where the guest is
 * FRAMING, no shutter this page owns, and no frozen frame to find a face in
 * before anything is uploaded. What the guest saw instead was a scanning
 * animation over a preview that was not being scanned.
 *
 * So the camera is opened here, and this hook owns the two things that go wrong
 * with one: PERMISSION, which may be refused and must be said out loud rather
 * than left as a blank rectangle, and RELEASE, because a MediaStream nobody
 * stopped keeps the phone's camera light on after the sheet has closed.
 *
 * MIRRORING. The live preview is mirrored, because framing yourself in a
 * picture that moves the wrong way is genuinely hard. The CAPTURE is not: the
 * bytes are the true image, which is what the detector sees, what the search
 * embeds, and what the frozen frame and its crop show. One set of pixels
 * through the whole flow — the alternative is a guest looking at a crop that is
 * not the crop the results came from.
 */

export type SelfieCameraStatus =
  /** Nothing asked for yet. */
  | 'idle'
  /** getUserMedia is in flight — usually the permission prompt. */
  | 'starting'
  /** A stream is attached and the preview is moving. */
  | 'live'
  /** The person said no. Recoverable only by them, in browser settings. */
  | 'denied'
  /** No camera, no getUserMedia, or an insecure context. Not their fault. */
  | 'unavailable';

export interface SelfieCamera {
  status: SelfieCameraStatus;
  /** Attach to the <video> that shows the preview. */
  videoRef: React.RefObject<HTMLVideoElement | null>;
  start(): void;
  /** Stop every track and drop the stream. Idempotent. */
  stop(): void;
  /**
   * The current frame as a JPEG file, or null when there is nothing to grab.
   *
   * NOT mirrored: see the note above. The file exists only for the length of
   * one search — the caller revokes it and drops the reference.
   */
  capture(): Promise<File | null>;
}

/** What the capture aims for. Above this the sheet uploads more than it needs. */
const CAPTURE_MAX_DIMENSION = 1280;
const CAPTURE_QUALITY = 0.9;

export function useSelfieCamera(): SelfieCamera {
  const [status, setStatus] = useState<SelfieCameraStatus>('idle');
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const streamRef = useRef<MediaStream | null>(null);
  // Every start is numbered, so a stream that arrives after the guest closed
  // the sheet (or started again) is stopped instead of attached. Without this a
  // slow permission prompt leaves the camera light on over a closed overlay.
  const attemptRef = useRef(0);

  const stop = useCallback(() => {
    attemptRef.current += 1;
    const stream = streamRef.current;
    streamRef.current = null;
    if (videoRef.current) videoRef.current.srcObject = null;
    stream?.getTracks().forEach((track) => {
      try { track.stop(); } catch { /* already ended */ }
    });
    setStatus('idle');
  }, []);

  const start = useCallback(() => {
    const media = typeof navigator === 'undefined' ? undefined : navigator.mediaDevices;
    if (!media || typeof media.getUserMedia !== 'function') {
      // No camera API at all: an old browser, or a page served over plain
      // http. The caller falls back to the operating system's own camera.
      setStatus('unavailable');
      return;
    }

    attemptRef.current += 1;
    const attempt = attemptRef.current;
    setStatus('starting');
    void media
      .getUserMedia({
        // `facingMode: 'user'` and not `exact`: a laptop has one camera and
        // would refuse an exact front-facing constraint outright.
        video: { facingMode: 'user', width: { ideal: 1280 }, height: { ideal: 1280 } },
        audio: false,
      })
      .then((stream) => {
        if (attempt !== attemptRef.current) {
          stream.getTracks().forEach((track) => track.stop());
          return;
        }
        streamRef.current = stream;
        if (videoRef.current) {
          videoRef.current.srcObject = stream;
          // Autoplay needs the promise handled: iOS rejects it when the
          // element is not muted and playsInline, both of which the caller
          // sets — this is the belt for the braces.
          void videoRef.current.play().catch(() => { /* the poster frame stands */ });
        }
        setStatus('live');
      })
      .catch((err: unknown) => {
        if (attempt !== attemptRef.current) return;
        // NotAllowedError is a person saying no; everything else is the device
        // or the browser. They are different sentences, so they are different
        // states rather than one "camera error".
        const name = err && typeof err === 'object' && 'name' in err
          ? String((err as { name?: unknown }).name)
          : '';
        setStatus(name === 'NotAllowedError' || name === 'SecurityError' ? 'denied' : 'unavailable');
      });
  }, []);

  const capture = useCallback(async (): Promise<File | null> => {
    const video = videoRef.current;
    if (!video || typeof document === 'undefined') return null;
    const width = video.videoWidth;
    const height = video.videoHeight;
    if (!width || !height) return null;

    const scale = Math.min(1, CAPTURE_MAX_DIMENSION / Math.max(width, height));
    const canvas = document.createElement('canvas');
    canvas.width = Math.max(1, Math.round(width * scale));
    canvas.height = Math.max(1, Math.round(height * scale));
    const ctx = canvas.getContext('2d');
    if (!ctx) return null;
    // Drawn straight, never flipped: the preview's mirror is a CSS transform on
    // the video element and stops here.
    ctx.drawImage(video, 0, 0, canvas.width, canvas.height);

    const blob = await new Promise<Blob | null>((resolve) => {
      if (typeof canvas.toBlob !== 'function') { resolve(null); return; }
      canvas.toBlob(resolve, 'image/jpeg', CAPTURE_QUALITY);
    });
    if (!blob || blob.size === 0) return null;
    return new File([blob], 'selfie.jpg', { type: 'image/jpeg' });
  }, []);

  // The camera is released when the component holding it goes away, whatever
  // took it away: the sheet closing, a route change, or an error path that
  // forgot. A camera light left on is the failure guests notice.
  useEffect(() => stop, [stop]);

  return { status, videoRef, start, stop, capture };
}
