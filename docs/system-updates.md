# System updates

An administrator updates an installation from **Administration › Updates**:
**Check for updates** shows what the newest release on `main` would change on
this server, and **Install** installs exactly that release. Print Boxes follow
the server from the same page. Every installation updates itself the same way,
so many installations are updated by pressing the same button on each one.

This page is the one-time setup and the model behind it. The deploy gates
themselves are unchanged and documented in
[`deploy/FAST_DEPLOY.md`](../deploy/FAST_DEPLOY.md).

## How a release reaches a server

1. **Every push to `main` becomes a release.** The `Release` workflow
   (`.github/workflows/release.yml`) rebuilds only the components whose files
   changed (`deploy/release-components.json`): the API (both the OpenVINO and
   the lean runtime image), the frontend, and the Linux Print Agent bundle.
   It then writes a **release manifest**: for each component, the image by
   digest and the commit it was built from. A freshly built image is named by
   the digest its build job pushed. A component that did not change keeps the
   entry of the **previous signed release**, never an image found by its tag
   in the registry. A server accepts an earlier image only after proving with
   `git diff` that none of the component's files changed since that commit
   (`deploy/release-manifest.py verify`). The component's files are its whole
   build input: a test checks them against its Dockerfile, project and
   workflow.
2. **The manifest is signed** with Sigstore `cosign` and published as the
   tiny image `ghcr.io/littlefly1969/nubarca-release:<commit>`.
3. **The server checks** (`deploy/update-production.sh check`). It fetches
   `origin/main`, pulls that commit's manifest, verifies the signature, checks
   every image against the trusted GHCR repository, and reports what would
   change: components, migrations and commits. A release whose images are
   still building reports *not published yet*. The check also reports the
   **release id**: a hash of the signed manifest's bytes and of the TV bundle
   digests it found.
4. **The server installs** (`deploy/update-production.sh apply`) exactly the
   release the check found: the install names the release id, and anything
   that changed since (a re-pointed tag, a republished TV bundle) is refused
   with *run check again*. CI publishes a commit's release once and never
   replaces it. The install runs with every
   existing gate. That covers the migration policy, a backup before any
   migration, the image verifiers, smoke checks and the rollback. A failed
   install puts the whole previous release back: the checkout returns to the
   commit it came from (its Compose files, scripts and trust), then the image
   pins, then the previous containers restart and must answer healthy again.
   Approved additive migrations stay applied.
   It then publishes the new Print Agent bundle and, where enabled, the TV
   bundle.

The API never runs any of this. It only drops a request into a mailbox that the
**host updater** reads. The host updater is a small service on the host,
outside every container (`deploy/updater/nubarca-updater.py`). It runs the
same script an operator runs by hand, under the checkout owner's account, never
as root. Because it lives outside the containers, it survives the API being
recreated mid-install, and it reports the outcome when the new API starts.
After an install it restarts from the updated checkout, so it updates itself
too.

The updater also checks on its own every 6 hours by default
(`--auto-check-hours`, where 0 means never). It never installs on its own.

## One-time setup on each server

From the production checkout, once:

```bash
git pull --ff-only origin main
sudo ./deploy/updater/install-updater.sh --env-file .env --mailbox <directory>
```

The command does three things:

- It installs the pinned `cosign`, checking its checksum.
- It creates the mailbox and records it in `.env` as `NUBARCA_UPDATER_DIR`.
- It installs and starts `nubarca-updater.service`.

The command then prints the one command that recreates the API so it mounts the mailbox.

Two optional `.env` settings describe the installation:

| setting | values | meaning |
| --- | --- | --- |
| `NUBARCA_API_VARIANT` | `openvino` (default) or `runtime` | `openvino` uses the Intel GPU image and the four-file Compose stack. `runtime` is the lean, CPU-only image without `docker-compose.facedirect-api.yml`. |
| `NUBARCA_TV_ENABLED` | `true` (default) or `false` | `false` means this installation never publishes TV bundles, on purpose. |

The server must reach `ghcr.io`, GitHub and, for keyless signatures,
`tuf-repo-cdn.sigstore.dev`. That last host serves Sigstore's trust root.

## Signatures: keyless or a NubArca key

There are two signing modes. Each server uses the one that its installed
checkout declares.

- **Keyless (default).** No file and no secret are needed. The manifest
  carries a short-lived certificate tying it to
  `.github/workflows/release.yml@refs/heads/main` in this repository. The
  server checks that identity and the GitHub OIDC issuer.
- **A NubArca key.** Commit the public key as `deploy/release-signing.pub`.
  Set the repository secrets `RELEASE_SIGNING_KEY` (the `cosign`-encrypted
  private key) and `RELEASE_SIGNING_KEY_PASSWORD`. Releases are then signed
  with that key, without a public transparency-log entry, and servers verify
  them against the committed public key. This suits a **private repository**.

### Changing the signer

A server accepts a new signer only when the signer it already trusts
authorized it. Every manifest names its signer, the release that introduced
that signer, and the release before it. When a commit changes the signer
(adds, replaces or removes `deploy/release-signing.pub`), its release is a
**trust transition**: CI signs it twice, once with the new signer and once
with the previous one (`release.transition.sigstore.json`). A server walks
back from the candidate to the signer its installed checkout holds and then
proves every hand-over forwards (`deploy/release-manifest.py fetch-verified`).
A server many releases behind follows several transitions in one check. A
signer nobody handed over is refused, and so is a release still signed by a
key that was rotated out.

To switch from keyless to a key, or from one key to another:

1. Generate the key pair: `cosign generate-key-pair`.
2. Set `RELEASE_SIGNING_KEY` and `RELEASE_SIGNING_KEY_PASSWORD` to the new
   private key. For a rotation, first move the old private key and password
   to `RELEASE_SIGNING_KEY_PREVIOUS` and `RELEASE_SIGNING_KEY_PREVIOUS_PASSWORD`.
3. Commit the new public key as `deploy/release-signing.pub` and push it to
   `main`. Check that its Release run signed the hand-over.
4. After that release, the `_PREVIOUS` secrets can be deleted.

To return to keyless, delete `deploy/release-signing.pub` while
`RELEASE_SIGNING_KEY` still holds the current key, so it can sign the
hand-over.

### If the repository becomes private

- The servers' `git fetch` needs read access. Add a read-only **deploy key**
  to the repository and use it as the checkout's `origin`.
- The servers need to pull from GHCR. Either keep the packages public, or log
  in once on each server with a token that has `read:packages`
  (`docker login ghcr.io`). Note that a GHCR package made public cannot be made
  private again.
- Either signing mode keeps working. The key mode is the one that does not
  depend on public Sigstore infrastructure.

## Print Boxes

A Linux Print Box installs the Print Agent that **this server** publishes, and
only when someone asks:

- an administrator asks from **Updates › Print Boxes**, for one box or for all;
- a box's owner asks from its printer page, with **Update to X now**.

A request pins the exact bundle published at that moment: its version,
SHA-256 and size. The box is offered that bundle and nothing else. If another
bundle is published before the box installs, the request ends as
*superseded* and someone asks again. A request counts as done only when the
box reports exactly the pinned version.

The agent, between prints, rings a root service on the box
(`nubarca-print-agent-update.path`). It takes no new job for as long as that
service runs. That service fetches the pinned bundle **itself**, with the
station's credential, and checks its size and SHA-256 against what the server
names. It also checks that the new binary reports the offered version. It then
swaps the bundle in and keeps it only once the restarted agent reaches the
server again. Any other ending after the swap puts the previous bundle back:
no heartbeat, a failed unit install, an error or a stop signal. The page then
shows that the update failed. A failed update is not retried until someone
asks again.

Every request from the page is on record before it takes effect. A Print Box
request writes its audit record in the same transaction as the request. A
server check or install is written beside the updater's mailbox first, then
recorded, then delivered with one rename; if the audit record cannot be
written, nothing is delivered, and a delivery that fails after the record is
recorded as *not delivered*. The updater keeps a taken request as a journal
until it has published that request's state, so a crash in between shows the
request as *interrupted*, never as lost. An interrupted install is not re-run
until someone asks again.

Self-update arrives with **Print Agent 0.9.0**. Update each existing box by
hand once, re-running `install-print-box.sh` from the 0.9.0 bundle as the
printer page's update guide shows. From then on the box updates on request.
Windows agents keep the manual update.

## Logs and troubleshooting

- The updater's state is shown on the Updates page. It logs to
  `journalctl -u nubarca-updater`, and each run's full log stays below the
  checkout in `deploy-history/updater/`.
- *Not running* means the service is stopped, or the API does not mount the
  mailbox. Run `systemctl status nubarca-updater` and check that
  `NUBARCA_UPDATER_DIR` is in `.env` and that the API was recreated.
- A Print Box update logs to `journalctl -u nubarca-print-agent-update` on the
  box.
