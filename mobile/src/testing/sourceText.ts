// Reading SOURCE in tests.
//
// Several guarantees in the mobile client are structural — "the slide runs no
// hand-rolled touch engine", "no timeout arbitrates the pager" — and are
// asserted by checking a construct is ABSENT.
//
// That is where a negative assertion lies to you: `doesNotMatch` runs against
// the file's TEXT, so the comment explaining why a construct was retired keeps
// the assertion red, and a renamed construct keeps it green because the old
// name survives in prose. Both have happened in this repository — the TV
// client carries the same helper for the same reason.
//
// So reading strips comments, and there is no unstripped variant to reach for
// by accident. It is a lexical strip, not a parse: `//` inside a string
// literal goes too, which is why assertions target code shapes rather than URLs.
export function code(source: string): string {
  return source
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .split('\n')
    .filter((line) => !line.trimStart().startsWith('//'))
    .join('\n');
}
