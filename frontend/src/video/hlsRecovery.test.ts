import { describe, expect, it } from 'vitest';
import {
  EMPTY_BUDGET,
  MAX_MEDIA_RECOVERIES,
  MAX_NETWORK_RECOVERIES,
  type RecoveryBudget,
  classifyFatalError,
  planRecovery,
} from './hlsRecovery';

// hls.js's real ErrorTypes values, so the classifier is tested against the
// strings production actually receives rather than invented ones.
const TYPES = { NETWORK_ERROR: 'networkError', MEDIA_ERROR: 'mediaError' };

/** Feed a run of identical fatal errors through the policy. */
function run(kind: Parameters<typeof planRecovery>[0], times: number) {
  let budget: RecoveryBudget = EMPTY_BUDGET;
  const actions: string[] = [];
  for (let i = 0; i < times; i += 1) {
    const plan = planRecovery(kind, budget);
    budget = plan.budget;
    actions.push(plan.action);
  }
  return { actions, budget };
}

describe('classifyFatalError', () => {
  it('maps the two recoverable hls.js classes', () => {
    expect(classifyFatalError('networkError', TYPES)).toBe('network');
    expect(classifyFatalError('mediaError', TYPES)).toBe('media');
  });

  it('treats anything else as unrecoverable rather than guessing', () => {
    expect(classifyFatalError('otherError', TYPES)).toBe('other');
    expect(classifyFatalError('muxError', TYPES)).toBe('other');
    expect(classifyFatalError('', TYPES)).toBe('other');
  });
});

describe('planRecovery — network', () => {
  it('restarts loading while budget remains', () => {
    const { actions } = run('network', MAX_NETWORK_RECOVERIES);
    expect(actions).toEqual(Array(MAX_NETWORK_RECOVERIES).fill('restart-load'));
  });

  it('gives up once the budget is spent — no infinite retry', () => {
    const { actions, budget } = run('network', MAX_NETWORK_RECOVERIES + 5);
    expect(actions.slice(0, MAX_NETWORK_RECOVERIES))
      .toEqual(Array(MAX_NETWORK_RECOVERIES).fill('restart-load'));
    expect(actions.slice(MAX_NETWORK_RECOVERIES)).toEqual(Array(5).fill('give-up'));
    // The budget stops counting once exhausted, so it cannot overflow.
    expect(budget.network).toBe(MAX_NETWORK_RECOVERIES);
  });
});

describe('planRecovery — media', () => {
  it('recovers while budget remains, then gives up', () => {
    const { actions } = run('media', MAX_MEDIA_RECOVERIES + 2);
    expect(actions.slice(0, MAX_MEDIA_RECOVERIES))
      .toEqual(Array(MAX_MEDIA_RECOVERIES).fill('recover-media'));
    expect(actions.slice(MAX_MEDIA_RECOVERIES)).toEqual(['give-up', 'give-up']);
  });
});

describe('planRecovery — budgets are independent', () => {
  it('does not let a spent network budget block a media recovery', () => {
    let budget: RecoveryBudget = { network: MAX_NETWORK_RECOVERIES, media: 0 };
    const plan = planRecovery('media', budget);
    expect(plan.action).toBe('recover-media');
    budget = plan.budget;
    expect(budget).toEqual({ network: MAX_NETWORK_RECOVERIES, media: 1 });
  });

  it('does not let a spent media budget block a network retry', () => {
    const plan = planRecovery('network', { network: 0, media: MAX_MEDIA_RECOVERIES });
    expect(plan.action).toBe('restart-load');
  });
});

describe('planRecovery — unrecoverable', () => {
  it('never retries an unknown fatal class, and spends nothing', () => {
    // A 401 on a segment or a deleted file arrives here. Retrying would hide
    // a real authorization/404 failure behind a loop.
    const plan = planRecovery('other', EMPTY_BUDGET);
    expect(plan.action).toBe('give-up');
    expect(plan.budget).toEqual(EMPTY_BUDGET);
  });
});

describe('planRecovery — purity', () => {
  it('does not mutate the budget it is given', () => {
    const before: RecoveryBudget = { network: 1, media: 1 };
    const snapshot = { ...before };
    planRecovery('network', before);
    planRecovery('media', before);
    expect(before).toEqual(snapshot);
  });
});
