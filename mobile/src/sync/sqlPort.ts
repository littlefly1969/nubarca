// Minimal synchronous SQLite seam.
//
// The sync ledger is plain SQL so it can be exercised for REAL by `node
// --test` through node:sqlite's DatabaseSync, while production binds the very
// same interface to expo-sqlite's openDatabaseSync. Both drivers support the
// tiny surface below; nothing else about either leaks into sync logic.

export type SqlValue = string | number | null;
export type SqlRow = Record<string, SqlValue>;

export interface SqlStatement {
  run(...params: SqlValue[]): { changes: number };
  get(...params: SqlValue[]): SqlRow | null;
  all(...params: SqlValue[]): SqlRow[];
}

export interface SqlConnection {
  exec(sql: string): void;
  prepare(sql: string): SqlStatement;
  close(): void;
}

/** Run `body` atomically; any throw rolls the whole thing back. */
export function withTransaction(conn: SqlConnection, body: () => void): void {
  conn.exec('BEGIN IMMEDIATE');
  try {
    body();
    conn.exec('COMMIT');
  } catch (err) {
    try {
      conn.exec('ROLLBACK');
    } catch {
      // A broken connection cannot roll back either; rethrow the ORIGINAL.
    }
    throw err;
  }
}
