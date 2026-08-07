// Extends Vitest's `expect` with the matchers from @testing-library/jest-dom
// (toBeInTheDocument, toBeDisabled, toHaveTextContent, …). Side-effect import
// — keep the unused-import lint quiet by referencing nothing.
import '@testing-library/jest-dom/vitest';

// Tells React it's running inside a test renderer so `act(...)` warnings
// don't fire when test code (or its dependencies) wrap state updates.
declare global {
  // eslint-disable-next-line no-var
  var IS_REACT_ACT_ENVIRONMENT: boolean;
}
globalThis.IS_REACT_ACT_ENVIRONMENT = true;

// jsdom has no IntersectionObserver, which the gallery's infinite scroll
// (slice 80) relies on. Install a controllable mock: it records observed
// elements per instance, and `triggerIntersection()` (in test-utils) fires the
// callbacks of every active observer so tests can simulate the sentinel
// entering the viewport deterministically.
//
// The init is recorded too. Which ROOT an observer watches is not a detail once
// the application — rather than the document — owns the scrolling: a
// document-rooted observer inside `.app-main` has its preload margin swallowed by
// that container's clip, so `activeIntersectionObservers()` lets a test assert the
// root the sentinel was actually given.
type IOEntry = { isIntersecting: boolean; target: Element };
type IOCallback = (entries: IOEntry[], observer: unknown) => void;

export interface ObservedIntersection {
  root: IntersectionObserverInit['root'];
  rootMargin: IntersectionObserverInit['rootMargin'];
  elements: Element[];
}

class MockIntersectionObserver {
  static active: MockIntersectionObserver[] = [];
  private readonly cb: IOCallback;
  private readonly elements = new Set<Element>();
  readonly init: IntersectionObserverInit;
  constructor(cb: IOCallback, init: IntersectionObserverInit = {}) {
    this.cb = cb;
    this.init = init;
  }
  observe(el: Element) {
    this.elements.add(el);
    if (!MockIntersectionObserver.active.includes(this)) {
      MockIntersectionObserver.active.push(this);
    }
  }
  describe(): ObservedIntersection {
    return { root: this.init.root, rootMargin: this.init.rootMargin, elements: [...this.elements] };
  }
  unobserve(el: Element) {
    this.elements.delete(el);
  }
  disconnect() {
    this.elements.clear();
    MockIntersectionObserver.active = MockIntersectionObserver.active.filter((o) => o !== this);
  }
  takeRecords(): IOEntry[] {
    return [];
  }
  fire(isIntersecting: boolean) {
    this.cb([...this.elements].map((target) => ({ isIntersecting, target })), this);
  }
}

globalThis.IntersectionObserver = MockIntersectionObserver as unknown as typeof IntersectionObserver;
(globalThis as unknown as { __fireIntersection: (isIntersecting?: boolean) => void }).__fireIntersection =
  (isIntersecting = true) => {
    for (const observer of [...MockIntersectionObserver.active]) {
      observer.fire(isIntersecting);
    }
  };
(globalThis as unknown as { __activeIntersections: () => ObservedIntersection[] }).__activeIntersections =
  () => MockIntersectionObserver.active.map((observer) => observer.describe());
