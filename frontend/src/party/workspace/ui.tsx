import type { ReactNode } from 'react';
import { Link } from 'react-router';
import './PartyWorkspace.css';

// The Party workspace's vocabulary.
//
// Everything the host sees in a party is assembled from these: a section, a
// panel, a row, a switch, a notice, an empty state, a step. They exist so that
// "a card with a title and two buttons" is ONE thing in the product rather than
// eleven slightly different ones, and so that the rules that are easy to forget
// — 44px targets, a state that is never colour alone, one primary action per
// decision, a `<dl>` where a number has a label — are kept by construction.
//
// They are deliberately thin. None of them fetches, none of them knows about a
// party, and none of them holds state that is not purely presentational.

/* ── Buttons ──────────────────────────────────────────────────────────────── */

type ButtonTone = 'primary' | 'secondary' | 'quiet' | 'danger';

function buttonClass(tone: ButtonTone, size?: 'lg', block?: boolean, extra?: string): string {
  return [
    'pw-btn',
    tone === 'primary' ? 'pw-btn--primary' : '',
    tone === 'quiet' ? 'pw-btn--quiet' : '',
    tone === 'danger' ? 'pw-btn--danger' : '',
    size === 'lg' ? 'pw-btn--lg' : '',
    block ? 'pw-btn--block' : '',
    extra ?? '',
  ].filter(Boolean).join(' ');
}

export function Button({
  tone = 'secondary', size, block, busy, disabled, onClick, children, className,
  type = 'button', ...rest
}: {
  tone?: ButtonTone;
  size?: 'lg';
  block?: boolean;
  /** A request is in flight: the control stays readable and refuses a second press. */
  busy?: boolean;
  disabled?: boolean;
  onClick?: () => void;
  children: ReactNode;
  className?: string;
  type?: 'button' | 'submit';
} & Omit<React.ButtonHTMLAttributes<HTMLButtonElement>, 'onClick' | 'type' | 'className' | 'children'>) {
  return (
    <button
      // eslint-disable-next-line react/button-has-type -- narrowed to button|submit above
      type={type}
      className={buttonClass(tone, size, block, className)}
      // Double submission is prevented here rather than in every caller: while a
      // mutation is in flight the control is genuinely disabled, and it says so
      // to assistive technology instead of only looking faded.
      disabled={disabled || busy}
      data-busy={busy ? 'true' : undefined}
      aria-busy={busy || undefined}
      onClick={onClick}
      {...rest}
    >
      {children}
    </button>
  );
}

/** The same button, when pressing it navigates. */
export function ButtonLink({
  to, href, tone = 'secondary', size, block, children, className, ...rest
}: {
  to?: string;
  href?: string;
  tone?: ButtonTone;
  size?: 'lg';
  block?: boolean;
  children: ReactNode;
  className?: string;
} & Omit<React.AnchorHTMLAttributes<HTMLAnchorElement>, 'href' | 'className' | 'children'>) {
  const cls = buttonClass(tone, size, block, className);
  if (to) return <Link to={to} className={cls} {...rest}>{children}</Link>;
  return <a href={href} className={cls} {...rest}>{children}</a>;
}

/* ── Sections and panels ──────────────────────────────────────────────────── */

/**
 * A section's own heading: what this part of the party is, in one line, plus
 * whatever belongs beside the title rather than inside a panel.
 */
export function SectionHead({
  title, lede, actions, id,
}: {
  title: string;
  lede?: string;
  actions?: ReactNode;
  id?: string;
}) {
  return (
    <div className="pw-section-head">
      <div>
        <h2 className="pw-section-title" id={id}>{title}</h2>
        {lede && <p className="pw-section-lede">{lede}</p>}
      </div>
      {actions && <div className="pw-panel-actions">{actions}</div>}
    </div>
  );
}

export function Panel({
  title, note, aside, actions, children, tone, testId, className, headingLevel = 3,
}: {
  title?: string;
  note?: string;
  /** A badge or a count beside the title. */
  aside?: ReactNode;
  actions?: ReactNode;
  children?: ReactNode;
  tone?: 'quiet' | 'danger' | 'feature';
  testId?: string;
  className?: string;
  headingLevel?: 2 | 3 | 4;
}) {
  const Heading = `h${headingLevel}` as 'h2' | 'h3' | 'h4';
  return (
    <section
      className={[
        'pw-panel',
        tone ? `pw-panel--${tone}` : '',
        className ?? '',
      ].filter(Boolean).join(' ')}
      data-testid={testId}
    >
      {(title || aside) && (
        <div className="pw-panel-head">
          <div>
            {title && <Heading className="pw-panel-title">{title}</Heading>}
            {note && <p className="pw-panel-note">{note}</p>}
          </div>
          {aside}
        </div>
      )}
      {children && <div className="pw-panel-body">{children}</div>}
      {actions && <div className="pw-panel-actions">{actions}</div>}
    </section>
  );
}

/* ── Rows ─────────────────────────────────────────────────────────────────── */

export function Row({
  label, note, children, testId,
}: {
  label: ReactNode;
  note?: ReactNode;
  children?: ReactNode;
  testId?: string;
}) {
  return (
    <div className="pw-row" data-testid={testId}>
      <span className="pw-row-text">
        <span className="pw-row-label">{label}</span>
        {note && <span className="pw-row-note">{note}</span>}
      </span>
      {children && <span className="pw-row-control">{children}</span>}
    </div>
  );
}

/**
 * A switch with its own label and explanation.
 *
 * The control is a real `role="switch"` button rather than a checkbox in a
 * sentence: it has a 44px target, it announces on/off, and the explanation
 * below it is associated rather than floating nearby.
 */
export function SwitchRow({
  label, note, checked, disabled, onChange, testId,
}: {
  label: string;
  note?: string;
  checked: boolean;
  disabled?: boolean;
  onChange(next: boolean): void;
  /** Lands on the CONTROL — the row it sits in gets `${testId}-row`. */
  testId?: string;
}) {
  const noteId = note && testId ? `${testId}-note` : undefined;
  return (
    <div className="pw-row" data-testid={testId ? `${testId}-row` : undefined}>
      <span className="pw-row-text">
        <span className="pw-row-label">{label}</span>
        {note && <span className="pw-row-note" id={noteId}>{note}</span>}
      </span>
      <span className="pw-row-control">
        <button
          type="button"
          role="switch"
          className="pw-switch"
          aria-checked={checked}
          aria-label={label}
          aria-describedby={noteId}
          disabled={disabled}
          data-testid={testId}
          onClick={() => onChange(!checked)}
        />
      </span>
    </div>
  );
}

/** A whole row that opens somewhere else — the pattern for every "go to" link. */
export function LinkRow({
  to, href, title, note, after, testId, onClick,
}: {
  to?: string;
  href?: string;
  title: string;
  note?: ReactNode;
  /** A badge, a count, or nothing. The chevron is added regardless. */
  after?: ReactNode;
  testId?: string;
  onClick?: () => void;
}) {
  const inner = (
    <>
      <span className="pw-link-row-text">
        <span className="pw-link-row-title">{title}</span>
        {note && <span className="pw-link-row-note">{note}</span>}
      </span>
      <span className="pw-link-row-after">
        {after}
        <span className="pw-chevron" aria-hidden>›</span>
      </span>
    </>
  );
  if (to) {
    return <Link to={to} className="pw-link-row" data-testid={testId} onClick={onClick}>{inner}</Link>;
  }
  if (href) {
    return (
      <a
        className="pw-link-row" href={href} target="_blank" rel="noopener noreferrer"
        data-testid={testId} onClick={onClick}
      >
        {inner}
      </a>
    );
  }
  return (
    <button type="button" className="pw-link-row" data-testid={testId} onClick={onClick}>
      {inner}
    </button>
  );
}

/* ── State and feedback ───────────────────────────────────────────────────── */

export type NoticeTone = 'info' | 'warn' | 'error' | 'ok';

const NOTICE_MARK: Record<NoticeTone, string> = {
  info: 'ℹ', warn: '!', error: '✕', ok: '✓',
};

/**
 * Information, a warning, a failure or a confirmation.
 *
 * The tone is carried by a mark and by the words, never by colour alone, and an
 * error is announced: a host who cannot distinguish red from grey still learns
 * that something failed.
 */
export function Notice({
  tone = 'info', title, children, actions, testId,
}: {
  tone?: NoticeTone;
  title?: string;
  children?: ReactNode;
  actions?: ReactNode;
  testId?: string;
}) {
  return (
    <div
      className={`pw-notice${tone === 'info' ? '' : ` pw-notice--${tone}`}`}
      role={tone === 'error' ? 'alert' : 'status'}
      data-testid={testId}
      data-tone={tone}
    >
      <span className="pw-notice-icon" aria-hidden>{NOTICE_MARK[tone]}</span>
      <div className="pw-notice-body">
        {title && <span className="pw-notice-title">{title}</span>}
        {children}
        {actions && <div className="pw-notice-actions">{actions}</div>}
      </div>
    </div>
  );
}

/**
 * Nothing here yet — said as an invitation rather than as a fault.
 *
 * `optional` is the sentence that stops a host wondering whether they have
 * broken something: some of a party's features are genuinely optional, and an
 * empty state that does not say so reads as missing data.
 */
export function EmptyState({
  title, body, optional, action, testId,
}: {
  title: string;
  body: string;
  optional?: string;
  action?: ReactNode;
  testId?: string;
}) {
  return (
    <div className="pw-empty" data-testid={testId}>
      <h3 className="pw-empty-title">{title}</h3>
      <p className="pw-empty-body">{body}</p>
      {optional && <p className="pw-empty-optional">{optional}</p>}
      {action}
    </div>
  );
}

export function Badge({
  kind, children, testId,
}: {
  kind: 'draft' | 'published' | 'live' | 'ended' | 'ok' | 'warn' | 'plain';
  children: ReactNode;
  testId?: string;
}) {
  return (
    <span className={`pw-badge pw-badge--${kind}`} data-testid={testId} data-kind={kind}>
      {children}
    </span>
  );
}

/* ── Choosing one of several ───────────────────────────────────────────────── */

/**
 * ONE choice out of several, as a card you can tap — and a real radio.
 *
 * <b>It is a native <code>&lt;input type="radio"&gt;</code> inside a
 * <code>&lt;label&gt;</code>, and that is the whole accessibility story.</b>
 * The browser gives the group a single tab stop, arrow keys that move the
 * selection, the right role and state for a screen reader, and a name to read
 * out — none of which a <code>div</code> with an <code>onClick</code> has, and
 * all of which a hand-rolled <code>role="radio"</code> would have to
 * reimplement and keep working. The card is what the radio LOOKS like, not a
 * replacement for it.
 *
 * A DISABLED option stays in the list rather than disappearing. "The printer
 * in the hall is offline" is information a host needs; a list that silently
 * shrank would leave them wondering where it went.
 */
export function ChoiceCard({
  name, value, checked, disabled, onSelect, title, meta, status, note, testId,
}: {
  name: string;
  value: string;
  checked: boolean;
  disabled?: boolean;
  onSelect(value: string): void;
  title: string;
  /** The quiet second line: where it is, what it is. */
  meta?: ReactNode;
  /** Its state, as a badge — never colour alone. */
  status?: ReactNode;
  /** Why it cannot be chosen, or what a host should know before choosing it. */
  note?: string;
  testId?: string;
}) {
  return (
    <label
      className="pw-choice"
      data-testid={testId ? `${testId}-card` : undefined}
      data-checked={checked ? 'true' : undefined}
      data-disabled={disabled ? 'true' : undefined}
    >
      <input
        className="pw-choice-input"
        type="radio"
        name={name}
        value={value}
        checked={checked}
        disabled={disabled}
        data-testid={testId}
        onChange={() => onSelect(value)}
      />
      <span className="pw-choice-mark" aria-hidden="true" />
      <span className="pw-choice-text">
        <span className="pw-choice-title">{title}</span>
        {meta && <span className="pw-choice-meta">{meta}</span>}
        {note && <span className="pw-choice-note">{note}</span>}
      </span>
      {status && <span className="pw-choice-status">{status}</span>}
    </label>
  );
}

/** The group the cards live in: one legend, one tab stop, arrow keys. */
export function ChoiceGroup({
  label, hint, children, testId,
}: {
  label: string;
  hint?: string;
  children: ReactNode;
  testId?: string;
}) {
  return (
    <fieldset className="pw-choices" data-testid={testId}>
      <legend className="pw-choices-legend">{label}</legend>
      {hint && <p className="pw-choices-hint">{hint}</p>}
      <div className="pw-choices-list">{children}</div>
    </fieldset>
  );
}

/** A number that IS the information. Rendered as a definition list, always labelled. */
export function Stats({
  label, items, testId,
}: {
  label: string;
  items: readonly {
    key: string;
    label: string;
    value: number | string;
    tone?: 'warn' | 'accent';
    /** The number opens what it counts: the tile becomes the way there. */
    onSelect?: () => void;
    testId?: string;
  }[];
  testId?: string;
}) {
  // Numbers that lead somewhere are buttons — one row to read and to tap,
  // rather than numbers above a second row of links naming the same things.
  if (items.some((item) => item.onSelect)) {
    return (
      <div className="pw-stats" role="group" aria-label={label} data-testid={testId}>
        {items.map((item) => (
          <button
            key={item.key} type="button" className="pw-stat pw-stat--link"
            data-metric={item.key} data-tone={item.tone} data-testid={item.testId}
            disabled={!item.onSelect} onClick={item.onSelect}
          >
            <span className="pw-stat-label">{item.label}</span>
            <span className="pw-stat-value">{item.value}</span>
          </button>
        ))}
      </div>
    );
  }
  return (
    <dl className="pw-stats" aria-label={label} data-testid={testId}>
      {items.map((item) => (
        <div key={item.key} className="pw-stat" data-metric={item.key} data-tone={item.tone}>
          <dt className="pw-stat-label">{item.label}</dt>
          <dd className="pw-stat-value">{item.value}</dd>
        </div>
      ))}
    </dl>
  );
}

/** One thing left to do, or one thing already done. */
export function Step({
  done, title, note, action, testId,
}: {
  done: boolean;
  title: string;
  note?: string;
  action?: ReactNode;
  testId?: string;
}) {
  return (
    <li className="pw-step" data-done={done} data-testid={testId}>
      <span className="pw-step-mark" aria-hidden>{done ? '✓' : ''}</span>
      <span className="pw-step-text">
        <span className="pw-step-title">{title}</span>
        {note && <span className="pw-step-note">{note}</span>}
        {/* The state is in the text too, not only in the mark's shape. */}
        <span className="visually-hidden">{done ? ' ✓' : ''}</span>
      </span>
      {action && <span className="pw-step-action">{action}</span>}
    </li>
  );
}

/** Advanced controls, folded away until the host asks for them. */
export function Disclosure({
  summary, note, children, testId, open,
}: {
  summary: string;
  note?: string;
  children: ReactNode;
  testId?: string;
  open?: boolean;
}) {
  return (
    <details className="pw-disclosure" data-testid={testId} open={open}>
      <summary>
        <span>
          {summary}
          {note && <span className="pw-disclosure-summary-note">{note}</span>}
        </span>
      </summary>
      <div className="pw-disclosure-body">{children}</div>
    </details>
  );
}

/** Content-shaped loading, so arriving data does not move the page. */
export function PanelSkeleton({ rows = 2 }: { rows?: number }) {
  return (
    <div className="pw-loading" aria-hidden>
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className="pw-skeleton pw-skeleton--panel" />
      ))}
    </div>
  );
}
