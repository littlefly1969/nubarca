#!/usr/bin/env python3
"""The host updater: runs deploy/update-production.sh when an administrator asks.

The API never controls Docker. It writes one request into a mailbox directory
it shares with this process (the api's /var/lib/nubarca/updater, the host's
NUBARCA_UPDATER_DIR); this process, a systemd service on the host outside every
container, validates the request and runs the same guided script an operator
runs by hand, with every gate that script has. Because it lives outside the
containers, it survives the api being recreated mid-update and reports the
outcome back through the mailbox when the new api starts.

Mailbox (all JSON, all written atomically):

  request.json  written by the api: {"id", "action": "check"|"apply",
                "sha" (apply), "confirmMigrations" (apply)}. Consumed here.
  status.json   written here: heartbeat, what runs now, the last outcome.
  report.json   written here: the last `check` report, as the script wrote it.

Only two actions exist, with arguments validated against fixed patterns; the
request can never name a command, a path or an image. What the api learns is
the script's structured report and one sanitized line on failure: no path,
credential or log. The full log stays on the host, below the checkout's
ignored deploy-history/updater/.

After an apply this process exits, and systemd restarts it from the
fast-forwarded checkout, so the updater updates itself with the release.
"""

import argparse
import datetime
import json
import os
import re
import subprocess
import sys
import time
import uuid

UPDATER_VERSION = 1
MAX_REQUEST_BYTES = 4096
MAX_REPORT_BYTES = 256 * 1024
REQUEST_ID = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")
COMMIT = re.compile(r"[0-9a-f]{40}")
CHECK_TIMEOUT_SECONDS = 20 * 60
APPLY_TIMEOUT_SECONDS = 3 * 60 * 60
KEEP_LOGS = 30


def now() -> str:
    return datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds")


def parse_time(value):
    try:
        return datetime.datetime.fromisoformat(value)
    except (TypeError, ValueError):
        return None


def write_json(path: str, document: dict) -> None:
    temporary = f"{path}.{os.getpid()}.tmp"
    with open(temporary, "w", encoding="utf-8") as handle:
        json.dump(document, handle, indent=2)
    os.chmod(temporary, 0o644)
    os.replace(temporary, path)


def read_json(path: str, limit: int):
    try:
        if os.path.getsize(path) > limit:
            return None
        with open(path, encoding="utf-8") as handle:
            value = json.load(handle)
        return value if isinstance(value, dict) else None
    except (OSError, ValueError):
        return None


ABSOLUTE_PATH = re.compile(r"(?<![\w.])/(?:[\w.@+-]+/?)+")
SECRETISH = re.compile(r"(?i)(password|secret|token|key)=\S+")


def sanitize(message: str) -> str:
    """One line, no path, nothing that looks like a credential."""
    line = " ".join((message or "").split())
    line = SECRETISH.sub(r"\1=…", line)
    line = ABSOLUTE_PATH.sub("…", line)
    return line[:300]


def validate_request(request) -> tuple:
    """(request, None) when valid, else (None, reason)."""
    if not isinstance(request, dict):
        return None, "the request is not readable"
    request_id = request.get("id")
    action = request.get("action")
    if not isinstance(request_id, str) or not REQUEST_ID.fullmatch(request_id):
        return None, "the request has no valid id"
    if action == "check":
        return {"id": request_id, "action": "check"}, None
    if action == "apply":
        sha = request.get("sha")
        confirm = request.get("confirmMigrations")
        if not isinstance(sha, str) or not COMMIT.fullmatch(sha):
            return None, "an install request must name the full commit it confirms"
        if not isinstance(confirm, bool):
            return None, "an install request must say whether it confirms migrations"
        return {"id": request_id, "action": "apply", "sha": sha, "confirmMigrations": confirm}, None
    return None, "unknown action"


class Updater:
    def __init__(self, mailbox: str, checkout: str, env_file: str, script: str,
                 auto_check_hours: float, log_dir: str):
        self.mailbox = mailbox
        self.checkout = checkout
        self.env_file = env_file
        self.script = script
        self.auto_check_hours = auto_check_hours
        self.log_dir = log_dir
        self.status_path = os.path.join(mailbox, "status.json")
        self.report_path = os.path.join(mailbox, "report.json")
        self.request_path = os.path.join(mailbox, "request.json")
        previous = read_json(self.status_path, MAX_REPORT_BYTES) or {}
        last = previous.get("last") if isinstance(previous.get("last"), dict) else None
        # A run that was in progress when this process stopped did not finish here.
        if previous.get("state") in ("checking", "applying") and previous.get("current"):
            current = previous["current"]
            last = {
                **current,
                "outcome": "interrupted",
                "message": "the updater stopped before this run finished; check the server",
                "finishedAt": now(),
            }
        self.status = {
            "schemaVersion": 1,
            "updaterVersion": UPDATER_VERSION,
            "state": "idle",
            "current": None,
            "last": last,
            "lastCheckAt": previous.get("lastCheckAt"),
            "autoCheckHours": auto_check_hours,
        }

    def publish(self) -> None:
        self.status["heartbeatAt"] = now()
        write_json(self.status_path, self.status)

    def take_request(self):
        """Consumes the pending request, if any: (request, error)."""
        if not os.path.exists(self.request_path):
            return None, None
        processing = self.request_path + ".processing"
        try:
            os.replace(self.request_path, processing)
        except OSError:
            return None, None
        request = read_json(processing, MAX_REQUEST_BYTES)
        try:
            os.remove(processing)
        except OSError:
            pass
        validated, reason = validate_request(request)
        if validated is None:
            request_id = request.get("id") if isinstance(request, dict) else None
            return {"id": request_id if isinstance(request_id, str) and REQUEST_ID.fullmatch(request_id) else None,
                    "action": "invalid"}, reason
        return validated, None

    def auto_check_due(self) -> bool:
        if self.auto_check_hours <= 0:
            return False
        last = parse_time(self.status.get("lastCheckAt"))
        if last is None:
            return True
        return datetime.datetime.now(datetime.timezone.utc) - last > datetime.timedelta(hours=self.auto_check_hours)

    def command_for(self, request: dict, report: str) -> list:
        command = [self.script, request["action"], "--env-file", self.env_file, "--report-json", report]
        if request["action"] == "apply":
            command += ["--confirm", request["sha"]]
            if request["confirmMigrations"]:
                command.append("--confirm-migrations")
        return command

    def run(self, request: dict) -> dict:
        started = now()
        self.status["state"] = "checking" if request["action"] == "check" else "applying"
        self.status["current"] = {
            "requestId": request["id"],
            "action": request["action"],
            "sha": request.get("sha"),
            "automatic": request.get("automatic", False),
            "startedAt": started,
        }
        self.publish()
        os.makedirs(self.log_dir, mode=0o700, exist_ok=True)
        stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        log_path = os.path.join(self.log_dir, f"{stamp}-{request['action']}.log")
        report = os.path.join(self.log_dir, f"{stamp}-{request['action']}.report.json")
        timeout = CHECK_TIMEOUT_SECONDS if request["action"] == "check" else APPLY_TIMEOUT_SECONDS
        exit_code = None
        with open(log_path, "w", encoding="utf-8") as log:
            os.chmod(log_path, 0o600)
            try:
                process = subprocess.Popen(self.command_for(request, report), cwd=self.checkout,
                                           stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL)
                deadline = time.monotonic() + timeout
                while process.poll() is None:
                    if time.monotonic() > deadline:
                        process.terminate()
                        try:
                            process.wait(30)
                        except subprocess.TimeoutExpired:
                            process.kill()
                        break
                    # The heartbeat goes on while a long apply runs.
                    self.publish()
                    time.sleep(5)
                exit_code = process.returncode
            except OSError as error:
                log.write(f"cannot start the update script: {error}\n")
        result = read_json(report, MAX_REPORT_BYTES) or {}
        outcome = result.get("outcome") if isinstance(result.get("outcome"), str) else None
        if exit_code is None:
            outcome, message = "failed", "the update script did not run"
        elif outcome is None:
            outcome = "failed"
            message = "the update script stopped without a report" if exit_code != 0 else "no report"
        else:
            message = result.get("message") if isinstance(result.get("message"), str) else None
        if exit_code is not None and exit_code < 0:
            outcome, message = "failed", "the update took too long and was stopped"
        last = {
            "requestId": request["id"],
            "action": request["action"],
            "sha": request.get("sha"),
            "automatic": request.get("automatic", False),
            "outcome": outcome,
            "message": sanitize(message) if message else None,
            "startedAt": started,
            "finishedAt": now(),
        }
        if request["action"] == "check" and outcome in ("ready", "up-to-date", "not-deployable"):
            write_json(self.report_path, result)
            self.status["lastCheckAt"] = last["finishedAt"]
        elif request["action"] == "apply" and outcome == "applied":
            # What was just installed is the new baseline; the old report is stale.
            try:
                os.remove(self.report_path)
            except OSError:
                pass
        self.status["state"] = "idle"
        self.status["current"] = None
        self.status["last"] = last
        self.publish()
        self.prune_logs()
        return last

    def fail_request(self, request: dict, reason: str) -> None:
        self.status["last"] = {
            "requestId": request.get("id"),
            "action": request.get("action"),
            "sha": None,
            "automatic": False,
            "outcome": "rejected",
            "message": sanitize(reason),
            "startedAt": now(),
            "finishedAt": now(),
        }
        self.publish()

    def prune_logs(self) -> None:
        try:
            names = sorted(n for n in os.listdir(self.log_dir) if n.endswith(".log") or n.endswith(".json"))
        except OSError:
            return
        for name in names[:-KEEP_LOGS * 2]:
            try:
                os.remove(os.path.join(self.log_dir, name))
            except OSError:
                pass

    def step(self) -> str | None:
        """One poll: returns the action run, if any."""
        request, error = self.take_request()
        if request is not None and error is not None:
            self.fail_request(request, error)
            return "rejected"
        if request is None and self.auto_check_due():
            request = {"id": str(uuid.uuid4()), "action": "check", "automatic": True}
        if request is None:
            self.publish()
            return None
        self.run(request)
        return request["action"]


def main(argv: list) -> int:
    parser = argparse.ArgumentParser(description="NubArca host updater")
    parser.add_argument("--mailbox", required=True)
    parser.add_argument("--checkout", required=True)
    parser.add_argument("--env-file", required=True)
    parser.add_argument("--script", help="defaults to <checkout>/deploy/update-production.sh")
    parser.add_argument("--log-dir", help="defaults to <checkout>/deploy-history/updater")
    parser.add_argument("--auto-check-hours", type=float, default=6.0)
    parser.add_argument("--poll-seconds", type=float, default=5.0)
    parser.add_argument("--once", action="store_true", help="handle at most one request, then exit")
    args = parser.parse_args(argv)
    for name in ("mailbox", "checkout", "env_file"):
        if not os.path.isabs(getattr(args, name)):
            parser.error(f"--{name.replace('_', '-')} must be an absolute path")
    if not os.path.isdir(args.mailbox):
        parser.error("the mailbox directory does not exist")
    script = args.script or os.path.join(args.checkout, "deploy", "update-production.sh")
    log_dir = args.log_dir or os.path.join(args.checkout, "deploy-history", "updater")
    os.umask(0o022)
    updater = Updater(args.mailbox, args.checkout, args.env_file, script, args.auto_check_hours, log_dir)
    updater.publish()
    while True:
        action = updater.step()
        if args.once and action is not None:
            return 0
        # A new release runs a new updater: systemd restarts this one from the new checkout.
        if action == "apply" and updater.status["last"]["outcome"] in ("applied", "failed"):
            return 0
        time.sleep(args.poll_seconds)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
