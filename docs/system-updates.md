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
   digest and the commit it was built from. A component that did not change
   keeps its earlier image. A server accepts that only after proving with
   `git diff` that none of the component's files changed since that commit
   (`deploy/release-manifest.py verify`).
2. **The manifest is signed** with Sigstore `cosign` and published as the
   tiny image `ghcr.io/littlefly1969/nubarca-release:<commit>`.
3. **The server checks** (`deploy/update-production.sh check`). It fetches
   `origin/main`, pulls that commit's manifest, verifies the signature, checks
   every image against the trusted GHCR repository, and reports what would
   change: components, migrations and commits. A release whose images are
   still building reports *not published yet*.
4. **The server installs** (`deploy/update-production.sh apply`) with every
   existing gate. That covers the migration policy, a backup before any
   migration, the image verifiers, smoke checks and the rollback of the pins.
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

A new key takes effect only through a release that the previous mode signed. A
server always verifies a candidate with the checkout it is running now, so
nobody can swap the key in the same release they sign.

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

The agent, between prints, rings a root service on the box
(`nubarca-print-agent-update.path`) and takes no new job while the install may
restart it. That service fetches the offered bundle **itself**, with the
station's credential, and checks its size and SHA-256 against what the server
names. It also checks that the new binary reports the offered version. It then
swaps the bundle in and keeps it only once the restarted agent reaches the
server again. Otherwise it puts the previous bundle back, and the page shows
that the update failed. A failed update is not retried until someone asks
again.

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
