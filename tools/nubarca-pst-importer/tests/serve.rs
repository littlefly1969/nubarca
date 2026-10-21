//! `serve` protocol: ordered responses, request validation, work-dir rules.

mod common;

use std::io::{BufRead, BufReader, Write};
use std::path::Path;
use std::process::{Command, Stdio};

use common::*;
use serde_json::{json, Value};

fn serve(pst: &Path, work: &Path, requests: &[String]) -> Vec<Value> {
    let input: String = requests.iter().map(|r| format!("{r}\n")).collect();
    let out = run_with_input(
        &[
            "serve",
            "--pst",
            pst.to_str().unwrap(),
            "--work-dir",
            work.to_str().unwrap(),
        ],
        Some(&input),
    );
    assert_eq!(out.code, 0, "{}", out.stderr);
    out.events()
}

fn req(id: &str, mid: &str) -> String {
    json!({"op": "extract", "requestId": id, "messageId": mid}).to_string()
}

#[test]
fn many_requests_answered_in_order() {
    let pst = fixture("basic.pst");
    let ids = message_ids(&pst);
    let work = tempfile::tempdir().unwrap();
    let mut requests: Vec<String> = ids
        .iter()
        .enumerate()
        .map(|(i, m)| req(&format!("r{i}"), m))
        .collect();
    requests.push(json!({"op": "shutdown"}).to_string());
    let resp = serve(&pst, work.path(), &requests);
    assert_eq!(resp.len(), ids.len() + 1);
    for (i, (r, m)) in resp.iter().zip(&ids).enumerate() {
        let rid = format!("r{i}");
        assert_eq!(r["type"], "extracted", "{r}");
        assert_eq!(r["requestId"], rid.as_str());
        assert_eq!(r["messageId"], m.as_str());
        assert_eq!(r["document"], format!("{rid}/message.json"));
        let doc = work.path().join(r["document"].as_str().unwrap());
        assert_eq!(
            std::fs::metadata(&doc).unwrap().len(),
            r["documentBytes"].as_u64().unwrap()
        );
        for f in r["attachments"].as_array().unwrap() {
            let file = f["file"].as_str().unwrap();
            assert!(file.starts_with(&format!("{rid}/att-")), "{file}");
            assert_eq!(
                sha256_file(&work.path().join(file)),
                f["sha256"].as_str().unwrap()
            );
        }
    }
    assert_eq!(resp.last().unwrap()["type"], "shutdown");
    // Nothing outside the request directories.
    for entry in std::fs::read_dir(work.path()).unwrap() {
        let entry = entry.unwrap();
        assert!(entry.file_type().unwrap().is_dir());
        assert!(entry.file_name().to_str().unwrap().starts_with('r'));
    }
}

#[test]
fn bad_requests_do_not_stop_the_server() {
    let pst = fixture("basic.pst");
    let ids = message_ids(&pst);
    let work = tempfile::tempdir().unwrap();
    std::fs::create_dir(work.path().join("exists")).unwrap();
    let requests = vec![
        req("../escape", &ids[0]),
        req("a/b", &ids[0]),
        req("", &ids[0]),
        req(&"x".repeat(65), &ids[0]),
        req("dot.dot", &ids[0]),
        json!({"op": "extract", "requestId": 7, "messageId": ids[0]}).to_string(),
        req("badmid", "m:XYZ"),
        req("folderid", "f:000080a2"),
        req("missing", "m:00ffff04"),
        req("notmsg", "m:000080a2"),
        req("exists", &ids[0]),
        "this is not json".to_string(),
        json!({"op": "frobnicate"}).to_string(),
        format!(
            "{{\"op\":\"extract\",\"requestId\":\"big\",\"pad\":\"{}\"}}",
            "p".repeat(70_000)
        ),
        req("ok", &ids[0]),
    ];
    let resp = serve(&pst, work.path(), &requests);
    assert_eq!(
        resp.len(),
        requests.len(),
        "one response per request (EOF = shutdown)"
    );
    let codes: Vec<&str> = resp
        .iter()
        .map(|r| r["code"].as_str().unwrap_or(""))
        .collect();
    assert_eq!(
        codes,
        [
            "bad_request",
            "bad_request",
            "bad_request",
            "bad_request",
            "bad_request",
            "bad_request",
            "bad_request",
            "bad_request",
            "not_found",
            "not_found",
            "bad_request",
            "bad_request",
            "bad_request",
            "bad_request",
            ""
        ]
    );
    for r in &resp[..resp.len() - 1] {
        assert_eq!(r["type"], "error");
    }
    assert_eq!(resp[8]["requestId"], "missing");
    assert_eq!(resp[8]["messageId"], "m:00ffff04");
    assert_eq!(resp.last().unwrap()["type"], "extracted");
    // Rejected requests created nothing; failed ones cleaned up after themselves.
    let mut dirs: Vec<String> = std::fs::read_dir(work.path())
        .unwrap()
        .map(|e| e.unwrap().file_name().into_string().unwrap())
        .collect();
    dirs.sort();
    assert_eq!(dirs, ["exists", "ok"]);
    assert!(!work.path().parent().unwrap().join("escape").exists());
}

#[test]
fn responses_are_flushed_one_by_one() {
    // Drive the server interactively: each response must arrive before the
    // next request is sent.
    let pst = fixture("basic.pst");
    let ids = message_ids(&pst);
    let work = tempfile::tempdir().unwrap();
    let mut child = Command::new(BIN)
        .args([
            "serve",
            "--pst",
            pst.to_str().unwrap(),
            "--work-dir",
            work.path().to_str().unwrap(),
        ])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    let mut stdin = child.stdin.take().unwrap();
    let mut stdout = BufReader::new(child.stdout.take().unwrap());
    for (i, m) in ids.iter().enumerate() {
        writeln!(stdin, "{}", req(&format!("i{i}"), m)).unwrap();
        stdin.flush().unwrap();
        let mut line = String::new();
        stdout.read_line(&mut line).unwrap();
        let v: Value = serde_json::from_str(&line).unwrap();
        assert_eq!(v["requestId"], format!("i{i}"));
    }
    drop(stdin); // EOF = shutdown
    let status = child.wait().unwrap();
    assert!(status.success());
}

#[test]
fn serve_requires_existing_work_dir_and_valid_pst() {
    let pst = fixture("basic.pst");
    let out = run(&[
        "serve",
        "--pst",
        pst.to_str().unwrap(),
        "--work-dir",
        "/nonexistent/dir",
    ]);
    assert_eq!(out.code, 2);
    assert_eq!(out.events()[0]["type"], "fatal");
    let work = tempfile::tempdir().unwrap();
    let bogus = work.path().join("bogus.pst");
    std::fs::write(&bogus, b"not a pst at all").unwrap();
    let out = run(&[
        "serve",
        "--pst",
        bogus.to_str().unwrap(),
        "--work-dir",
        work.path().to_str().unwrap(),
    ]);
    assert_eq!(out.code, 2);
    assert_eq!(out.events()[0]["code"], "unsupported");
}
