//! The engine confines itself before parsing (src/sandbox.rs). These tests
//! attempt, from inside the real binary, what the sandbox must forbid.

mod common;

use common::*;

fn check(
    work: &std::path::Path,
    outside_read: &std::path::Path,
    outside_dir: &std::path::Path,
) -> serde_json::Value {
    let pst = fixture("basic.pst");
    let out = run(&[
        "sandbox-check",
        "--pst",
        pst.to_str().unwrap(),
        "--work-dir",
        work.to_str().unwrap(),
        "--outside-read",
        outside_read.to_str().unwrap(),
        "--outside-dir",
        outside_dir.to_str().unwrap(),
    ]);
    assert_eq!(out.code, 0, "{}", out.stderr);
    out.events().remove(0)
}

#[cfg(target_os = "linux")]
#[test]
fn no_network_no_exec_and_no_filesystem_outside_the_allowed_paths() {
    let dir = tempfile::tempdir().unwrap();
    let work = dir.path().join("work");
    let outside = dir.path().join("outside");
    std::fs::create_dir_all(&work).unwrap();
    std::fs::create_dir_all(&outside).unwrap();
    let secret = outside.join("secret.txt");
    std::fs::write(&secret, b"not for the parser").unwrap();

    let event = check(&work, &secret, &outside);
    let report = &event["report"];
    assert_eq!(report["rlimits"], true);
    assert_eq!(report["noNewPrivs"], true);
    assert_eq!(report["seccomp"], true);
    let checks = &event["checks"];
    assert_eq!(checks["network"], "denied");
    assert_eq!(checks["exec"], "denied");
    assert_eq!(checks["writeInside"], "allowed");
    if report["landlock"] == "unavailable" {
        eprintln!("kernel without Landlock: filesystem confinement not asserted");
        return;
    }
    assert_eq!(checks["readOutside"], "denied");
    assert_eq!(checks["writeOutside"], "denied");
    assert!(!outside.join("sandbox-check.tmp").exists());
}

#[cfg(target_os = "linux")]
#[test]
fn landlock_required_refuses_to_run_unconfined_or_runs_confined() {
    let pst = fixture("basic.pst");
    let out = run(&[
        "probe",
        "--pst",
        pst.to_str().unwrap(),
        "--landlock",
        "required",
    ]);
    let event = out.events().remove(0);
    if out.code == 0 {
        assert_ne!(event["sandbox"]["landlock"], "unavailable");
    } else {
        assert_eq!(out.code, 2);
        assert_eq!(event["type"], "fatal");
        assert_eq!(event["code"], "sandbox_unavailable");
    }
}

#[test]
fn probe_reports_the_sandbox_it_runs_under() {
    let pst = fixture("basic.pst");
    let out = run(&["probe", "--pst", pst.to_str().unwrap()]);
    assert_eq!(out.code, 0, "{}", out.stderr);
    let sandbox = &out.events()[0]["sandbox"];
    assert!(sandbox["landlock"].is_string());
    assert!(sandbox["capabilities"].is_string());
}

#[test]
fn bad_landlock_value_is_a_usage_error() {
    let pst = fixture("basic.pst");
    let out = run(&[
        "probe",
        "--pst",
        pst.to_str().unwrap(),
        "--landlock",
        "maybe",
    ]);
    assert_eq!(out.code, 3);
}

#[test]
fn run_as_root_is_a_usage_error() {
    let pst = fixture("basic.pst");
    for value in ["0:0", "65534:0", "nobody", "65534"] {
        let out = run(&["probe", "--pst", pst.to_str().unwrap(), "--run-as", value]);
        assert_eq!(out.code, 3, "{value}");
    }
}

#[cfg(target_os = "linux")]
fn is_root() -> bool {
    std::fs::read_to_string("/proc/self/status")
        .unwrap()
        .lines()
        .any(|l| l.starts_with("Uid:") && l.split_whitespace().nth(2) == Some("0"))
}

/// Started as root (the worker container's default), the engine parses as
/// an unprivileged user and owns only its work directory.
#[cfg(target_os = "linux")]
#[test]
fn started_as_root_it_switches_to_the_run_as_user() {
    use std::os::unix::fs::{MetadataExt, PermissionsExt};
    if !is_root() {
        eprintln!("not root: the uid switch is not exercised");
        return;
    }
    let dir = tempfile::tempdir().unwrap();
    // As the worker's working area: traversable, not writable, by others.
    std::fs::set_permissions(dir.path(), std::fs::Permissions::from_mode(0o755)).unwrap();
    let pst = dir.path().join("basic.pst");
    std::fs::copy(fixture("basic.pst"), &pst).unwrap();
    let work = dir.path().join("work");
    let outside = dir.path().join("outside");
    std::fs::create_dir_all(&work).unwrap();
    std::fs::create_dir_all(&outside).unwrap();
    let secret = outside.join("secret.txt");
    std::fs::write(&secret, b"root only").unwrap();
    std::fs::set_permissions(&secret, std::fs::Permissions::from_mode(0o600)).unwrap();

    let out = run(&[
        "sandbox-check",
        "--pst",
        pst.to_str().unwrap(),
        "--work-dir",
        work.to_str().unwrap(),
        "--outside-read",
        secret.to_str().unwrap(),
        "--outside-dir",
        outside.to_str().unwrap(),
        "--run-as",
        "65534:65534",
        "--landlock",
        "best-effort",
    ]);
    assert_eq!(out.code, 0, "{}{}", out.stdout, out.stderr);
    let event = out.events().remove(0);
    assert_eq!(event["report"]["user"], "switched");
    assert_eq!(event["report"]["capabilities"], "dropped");
    let checks = &event["checks"];
    assert_eq!(checks["writeInside"], "allowed");
    // Denied by plain file permissions even without Landlock.
    assert_eq!(checks["readOutside"], "denied");
    assert_eq!(checks["writeOutside"], "denied");
    assert_eq!(std::fs::metadata(&work).unwrap().uid(), 65534);

    // A PST the unprivileged user cannot reach fails up front, as a fatal
    // sandbox error, not half-way through a parse.
    let closed = dir.path().join("closed");
    std::fs::create_dir_all(&closed).unwrap();
    let hidden = closed.join("basic.pst");
    std::fs::copy(fixture("basic.pst"), &hidden).unwrap();
    std::fs::set_permissions(&closed, std::fs::Permissions::from_mode(0o700)).unwrap();
    let out = run(&[
        "probe",
        "--pst",
        hidden.to_str().unwrap(),
        "--run-as",
        "65534:65534",
    ]);
    assert_eq!(out.code, 2, "{}", out.stdout);
    let event = out.events().remove(0);
    assert_eq!(event["type"], "fatal");
    assert_eq!(event["code"], "sandbox_unavailable");
}
