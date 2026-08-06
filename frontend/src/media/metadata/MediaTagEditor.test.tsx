import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { MediaTagEditor, MAX_TAGS, MAX_TAG_LENGTH } from './MediaTagEditor';
import { AuthedWrapper } from '../../test-utils';

// The chip editor must mirror the BACKEND tag rules exactly (32 tags, 64 chars,
// trim, case-insensitive dedupe keeping the first form) so the user never sees
// a save rejected for something the UI accepted.

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function Harness({ initial = [] as string[] }) {
  const [tags, setTags] = useState<string[]>(initial);
  return (
    <AuthedWrapper>
      <div>
        <MediaTagEditor tags={tags} onChange={setTags} />
        <output data-testid="value">{JSON.stringify(tags)}</output>
      </div>
    </AuthedWrapper>
  );
}

const value = () => JSON.parse(screen.getByTestId('value').textContent ?? '[]') as string[];

describe('MediaTagEditor', () => {
  it('adds a tag on Enter and renders it as a chip', async () => {
    const user = userEvent.setup();
    render(<Harness />);

    await user.type(screen.getByLabelText(/^Tag/), 'mare{Enter}');

    expect(value()).toEqual(['mare']);
    expect(screen.getByText('mare')).toBeInTheDocument();
    // The input is cleared, ready for the next tag.
    expect((screen.getByLabelText(/^Tag/) as HTMLInputElement).value).toBe('');
  });

  it('trims and ignores blank input', async () => {
    const user = userEvent.setup();
    render(<Harness />);

    await user.type(screen.getByLabelText(/^Tag/), '   mare   {Enter}');
    await user.type(screen.getByLabelText(/^Tag/), '   {Enter}');

    expect(value()).toEqual(['mare']);
  });

  it('refuses a case-insensitive duplicate and keeps the first form', async () => {
    const user = userEvent.setup();
    render(<Harness initial={['Mare']} />);

    await user.type(screen.getByLabelText(/^Tag/), 'mare{Enter}');

    expect(value()).toEqual(['Mare']);
    expect(screen.getByRole('alert')).toHaveTextContent('mare');
  });

  it('rejects a tag longer than the backend limit', async () => {
    const user = userEvent.setup();
    render(<Harness />);
    const input = screen.getByLabelText(/^Tag/) as HTMLInputElement;

    // maxLength stops typing past the limit; set the value directly to prove
    // the guard, not just the attribute.
    expect(input.maxLength).toBe(MAX_TAG_LENGTH);
    await user.type(input, 'x'.repeat(MAX_TAG_LENGTH));
    await user.type(input, '{Enter}');

    expect(value()).toEqual(['x'.repeat(MAX_TAG_LENGTH)]);
  });

  it('removes a single tag from its chip', async () => {
    const user = userEvent.setup();
    render(<Harness initial={['mare', 'montagna']} />);

    await user.click(screen.getByRole('button', { name: 'Rimuovi il tag mare' }));

    expect(value()).toEqual(['montagna']);
  });

  it('shows the n / 32 counter and stops at the cap', async () => {
    const user = userEvent.setup();
    const full = Array.from({ length: MAX_TAGS }, (_, i) => `t${i}`);
    render(<Harness initial={full} />);

    expect(screen.getByTestId('media-tag-count')).toHaveTextContent(`${MAX_TAGS} / ${MAX_TAGS}`);

    const input = screen.getByLabelText(/^Tag/) as HTMLInputElement;
    expect(input).toBeDisabled();
    await user.type(input, 'extra{Enter}');
    expect(value()).toHaveLength(MAX_TAGS);
  });

  it('updates the counter as tags are added', async () => {
    const user = userEvent.setup();
    render(<Harness />);

    expect(screen.getByTestId('media-tag-count')).toHaveTextContent(`0 / ${MAX_TAGS}`);
    await user.type(screen.getByLabelText(/^Tag/), 'a{Enter}b{Enter}');
    expect(screen.getByTestId('media-tag-count')).toHaveTextContent(`2 / ${MAX_TAGS}`);
  });

  it('commits a typed-but-unconfirmed tag on blur so it is not silently lost', async () => {
    const user = userEvent.setup();
    render(<Harness />);

    await user.type(screen.getByLabelText(/^Tag/), 'mare');
    await user.tab();

    expect(value()).toEqual(['mare']);
  });
});
