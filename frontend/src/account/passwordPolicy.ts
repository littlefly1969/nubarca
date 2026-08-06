// Mirrors NubArca.Api.Users.PasswordPolicy on the backend. The backend is
// the source of truth (this is just fast client-side feedback before a
// round-trip) — length bounds + not-all-whitespace, no complexity rules.
export const PasswordPolicy = {
  minLength: 10,
  maxLength: 256,

  isValid(password: string): boolean {
    const trimmed = password.trim();
    return (
      trimmed.length > 0
      && password.length >= PasswordPolicy.minLength
      && password.length <= PasswordPolicy.maxLength
    );
  },
};
