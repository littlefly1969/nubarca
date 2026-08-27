// Per-account SQLite ledger binding for expo-sqlite.
//
// One database file PER authenticated account under the app's private
// storage directory gives hard namespace isolation: account B physically
// cannot open account A's queue. The file name carries only an opaque
// account hash — no email, no display name. The session cookie itself
// stays exclusively in SecureStore.

import * as SQLite from 'expo-sqlite';
import type { SqlConnection } from './sqlPort.ts';
import type { SqlRow, SqlStatement, SqlValue } from './sqlPort.ts';
import { ensureLedgerSchema } from './syncLedger.ts';

function statementAdapter(statement: SQLite.SQLiteStatement): SqlStatement {
  return {
    run(...params: SqlValue[]) {
      const result = statement.executeSync(...params);
      return { changes: Number(result.changes ?? 0) };
    },
    get(...params: SqlValue[]): SqlRow | null {
      const result = statement.executeSync(...params);
      return (result.getFirstSync() as SqlRow | null) ?? null;
    },
    all(...params: SqlValue[]): SqlRow[] {
      const result = statement.executeSync(...params);
      return result.getAllSync() as SqlRow[];
    },
  };
}

export function openAccountLedgerConnection(accountId: string): SqlConnection {
  // Opaque, stable per-account namespace; hex digest avoids any filesystem-
  // sensitive characters from server ids.
  const namespace = fnv1aHex(accountId);
  const databaseName = `sync-ledger-${namespace}.db`;

  // openDatabaseSync takes a FILE NAME, not a file:// path. Expo resolves it
  // below defaultDatabaseDirectory and creates the parent synchronously before
  // opening. This removes the first-install race where an async mkdir and the
  // immediate synchronous open competed with one another.
  const db = SQLite.openDatabaseSync(databaseName);
  db.execSync('PRAGMA journal_mode = WAL');
  db.execSync('PRAGMA foreign_keys = OFF'); // ledger is self-contained

  const conn: SqlConnection = {
    exec(sql: string) {
      db.execSync(sql);
    },
    prepare(sql: string): SqlStatement {
      return statementAdapter(db.prepareSync(sql));
    },
    close() {
      db.closeSync();
    },
  };
  ensureLedgerSchema(conn);
  return conn;
}

function fnv1aHex(input: string): string {
  let hash = 0x811c9dc5;
  for (let i = 0; i < input.length; i++) {
    hash ^= input.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  const second = Math.imul(hash ^ 0x9e3779b9, 0x85ebca6b) >>> 0;
  return `${(hash >>> 0).toString(16).padStart(8, '0')}${second.toString(16).padStart(8, '0')}`;
}
