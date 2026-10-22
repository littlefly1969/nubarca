# #216 and #214 (MailArchive): compatibility check

Point 1 of the #216 review: prove that this PR and #214 can both land, in
either order, without a schema, policy or UI collision. Nothing here merges,
rebases or deploys anything. The check was a **local, unpublished** trial merge
of #214's head into #216's head.

Outcome: #214 was merged first (`main` `c5f6261`, 0.4.2) and #216 took `main` back in with the resolution below, identical to the trial merge of the final pair (#214 `29b81fd` + #216 `57575c3`), which also passed the checks in the table.

Heads first checked: #216 `19a6676`, #214 `251afea`, base `main` `682a01c`. Since then `main` gained #213 (0.4.1, every PR raises the version); #216 now carries 0.4.3, after #214's 0.4.2.

## Result

| Check | Result |
|---|---|
| Textual conflicts | 4, all "both added a line": `deploy/migration-policy.json`, `navModel.ts` (+ its test), `Icon.tsx`. Resolved by keeping both sides. No backend source conflict; `AppDbContext`, the model snapshot, `Program.cs`, `CliEntryPoint`, `JobConstants`, `JobScheduling` and i18n merged automatically. |
| Migration order | The ids interleave: `20261021181506_AddMailArchive` < `20261021224250_AddDocumentSourceIdentityAndIndexState` < `20261022033406_AddMailMessageTexts`. Neither PR touches the other's tables. |
| Migration policy | `deploy/production-migration-plan.py` approves the merged tree from `main`, from #216's head and from #214's head: in each case exactly the missing migrations, all `automated` and `previousApplicationCompatible`. |
| PostgreSQL apply, #216 first | `db migrate` with #216's build, then the merged build: the merged build applies `AddMailArchive` (older id) and `AddMailMessageTexts` after #216's migration. Completed. |
| PostgreSQL apply, #214 first | `db migrate` with #214's build, then the merged build applies #216's migration between them by id. Completed. |
| Same schema either way | `pg_dump -s` of the two databases is identical. |
| Model vs snapshot | `HasPendingModelChanges()` is false on the merged tree, so the auto-merged `AppDbContextModelSnapshot` is coherent and the next `migrations add` will not pick up a phantom diff. |
| Build and tests | Merged tree builds; frontend `lint` and the full vitest suite (3419) pass; identity contract holds. Backend, filtered to Mail, Document, Rag, Identity, Navigation, Deployment and Job tests: 1366 passed, 67 skipped, and 7 that need Docker for a PostgreSQL container (not available here), reported as failures by their fixture. |

PostgreSQL here is 16 with pgvector, not the production 17; the CI and the
production `check` remain the authority.

## What each order needs

- **#214 first (recommended).** It is older, self-contained and already
  versioned 0.4.2. #216 then takes `main` back in and resolves the four union
  conflicts above plus the CHANGELOG; it already carries 0.4.3. The
  production `check` will list one migration, #216's.
- **#216 first.** #214 resolves the same four conflicts and raises its
  version above 0.4.3. Its two migrations
  then have ids older than one already applied; EF Core applies pending
  migrations regardless, and the policy script accepts it, as tested above.

Either way the email source for document search stays a separate, later step,
owned by the MailArchive work: it starts after #216 is merged, through the
source contract (`docs/document-intelligence/source-contract.md`), with mail
staying owner-private and separate.
