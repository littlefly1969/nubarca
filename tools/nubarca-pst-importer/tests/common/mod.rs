//! Shared helpers for the integration tests: run the built binary with a
//! timeout, parse NDJSON, snapshot a file for the read-only guarantee.

#![allow(dead_code)]

use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant, SystemTime};

use serde_json::Value;
use sha2::{Digest, Sha256};

pub const BIN: &str = env!("CARGO_BIN_EXE_nubarca-pst-importer");

pub fn fixture(name: &str) -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("tests/fixtures")
        .join(name)
}

pub struct Output {
    pub code: i32,
    pub stdout: String,
    pub stderr: String,
}

impl Output {
    pub fn events(&self) -> Vec<Value> {
        parse_ndjson(&self.stdout)
    }
}

pub fn parse_ndjson(s: &str) -> Vec<Value> {
    s.lines()
        .map(|l| {
            serde_json::from_str(l).unwrap_or_else(|e| panic!("invalid NDJSON line {l:?}: {e}"))
        })
        .collect()
}

fn wait_with_timeout(mut child: Child, timeout: Duration) -> Output {
    let mut stdout = child.stdout.take().unwrap();
    let mut stderr = child.stderr.take().unwrap();
    let out_t = std::thread::spawn(move || {
        let mut s = String::new();
        stdout.read_to_string(&mut s).unwrap();
        s
    });
    let err_t = std::thread::spawn(move || {
        let mut s = String::new();
        stderr.read_to_string(&mut s).unwrap();
        s
    });
    let start = Instant::now();
    let status = loop {
        if let Some(status) = child.try_wait().unwrap() {
            break status;
        }
        if start.elapsed() > timeout {
            let _ = child.kill();
            panic!("engine did not finish within {timeout:?}");
        }
        std::thread::sleep(Duration::from_millis(5));
    };
    let code = status
        .code()
        .unwrap_or_else(|| panic!("engine killed by a signal: {status:?}"));
    Output {
        code,
        stdout: out_t.join().unwrap(),
        stderr: err_t.join().unwrap(),
    }
}

pub fn run(args: &[&str]) -> Output {
    run_with_input(args, None)
}

pub fn run_with_input(args: &[&str], input: Option<&str>) -> Output {
    run_full(args, input, &[], Duration::from_secs(120))
}

pub fn run_full(
    args: &[&str],
    input: Option<&str>,
    env: &[(&str, &str)],
    timeout: Duration,
) -> Output {
    let mut cmd = Command::new(BIN);
    cmd.args(args)
        .stdin(if input.is_some() {
            Stdio::piped()
        } else {
            Stdio::null()
        })
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    for (k, v) in env {
        cmd.env(k, v);
    }
    let mut child = cmd.spawn().expect("spawn engine");
    if let Some(input) = input {
        let mut stdin = child.stdin.take().unwrap();
        let input = input.to_string();
        std::thread::spawn(move || {
            let _ = stdin.write_all(input.as_bytes());
        });
    }
    wait_with_timeout(child, timeout)
}

pub fn peak_rss_kib(stderr: &str) -> Option<u64> {
    stderr
        .lines()
        .find_map(|l| l.strip_prefix("peak_rss_kib="))
        .and_then(|v| v.trim().parse().ok())
}

pub fn sha256_file(path: &Path) -> String {
    let data = std::fs::read(path).unwrap();
    Sha256::digest(&data)
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
}

/// Content hash, size and mtime of a file.
#[derive(Debug, PartialEq, Eq)]
pub struct Snapshot {
    sha256: String,
    len: u64,
    mtime: SystemTime,
}

pub fn snapshot(path: &Path) -> Snapshot {
    let meta = std::fs::metadata(path).unwrap();
    Snapshot {
        sha256: sha256_file(path),
        len: meta.len(),
        mtime: meta.modified().unwrap(),
    }
}

/// `list` the file and return its message ids in order.
pub fn message_ids(pst: &Path) -> Vec<String> {
    let out = run(&["list", "--pst", pst.to_str().unwrap()]);
    assert_eq!(out.code, 0, "{}", out.stderr);
    out.events()
        .into_iter()
        .filter(|e| e["type"] == "message")
        .map(|e| e["messageId"].as_str().unwrap().to_string())
        .collect()
}

/// Extract one message with `extract` into a fresh temp dir.
pub fn extract(pst: &Path, message_id: &str, extra: &[&str]) -> (Output, tempfile::TempDir) {
    let dir = tempfile::tempdir().unwrap();
    let out_dir = dir.path().join("out");
    let mut args = vec![
        "extract",
        "--pst",
        pst.to_str().unwrap(),
        "--message",
        message_id,
        "--out-dir",
        out_dir.to_str().unwrap(),
    ];
    args.extend_from_slice(extra);
    (run(&args), dir)
}

pub fn read_doc(dir: &tempfile::TempDir) -> Value {
    serde_json::from_slice(&std::fs::read(dir.path().join("out/message.json")).unwrap()).unwrap()
}
