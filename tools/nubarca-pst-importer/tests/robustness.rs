//! Hostile and broken inputs, the read-only guarantee and usage errors.

mod common;

use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::Duration;

use common::*;
use serde_json::{json, Value};

fn copy_fixture(dir: &Path, name: &str) -> PathBuf {
    let dst = dir.join(name);
    std::fs::copy(fixture(name), &dst).unwrap();
    dst
}

/// Every command on `pst` must finish, exit 0 or 2, and print valid NDJSON.
/// Returns the list events.
fn assert_graceful(pst: &Path) -> Vec<Value> {
    let p = pst.to_str().unwrap();
    let timeout = Duration::from_secs(30);
    let probe = run_full(&["probe", "--pst", p], None, &[], timeout);
    assert!(
        matches!(probe.code, 0 | 2),
        "probe exit {} {}",
        probe.code,
        probe.stderr
    );
    let ev = probe.events();
    assert_eq!(ev.len(), 1);
    assert!(ev[0]["type"] == "probe" || ev[0]["type"] == "fatal");

    let list = run_full(&["list", "--pst", p], None, &[], timeout);
    assert!(
        matches!(list.code, 0 | 2),
        "list exit {} {}",
        list.code,
        list.stderr
    );
    let events = list.events();
    let last = events.last().expect("list prints something");
    if list.code == 0 {
        assert_eq!(last["type"], "end");
    } else {
        assert_eq!(last["type"], "fatal");
    }

    // Extract every listed message (plus an unknown id) through one server.
    let mut ids: Vec<String> = events
        .iter()
        .filter(|e| e["type"] == "message")
        .map(|e| e["messageId"].as_str().unwrap().to_string())
        .collect();
    ids.push("m:00ffff04".into());
    let work = tempfile::tempdir().unwrap();
    let input: String = ids
        .iter()
        .enumerate()
        .map(|(i, m)| {
            format!(
                "{}\n",
                json!({"op":"extract","requestId":format!("q{i}"),"messageId":m})
            )
        })
        .collect();
    let serve = run_full(
        &[
            "serve",
            "--pst",
            p,
            "--work-dir",
            work.path().to_str().unwrap(),
        ],
        Some(&input),
        &[],
        timeout,
    );
    assert!(
        matches!(serve.code, 0 | 2),
        "serve exit {} {}",
        serve.code,
        serve.stderr
    );
    let resp = serve.events();
    if serve.code == 0 {
        assert_eq!(resp.len(), ids.len(), "one response per request");
        for (i, r) in resp.iter().enumerate() {
            assert_eq!(r["requestId"], format!("q{i}"));
            assert!(r["type"] == "extracted" || r["type"] == "error", "{r}");
            if r["type"] == "error" {
                let code = r["code"].as_str().unwrap();
                assert!(
                    [
                        "not_found",
                        "corrupt",
                        "too_large",
                        "unsupported",
                        "io_error",
                        "limit_exceeded"
                    ]
                    .contains(&code),
                    "{code}"
                );
            }
        }
    }
    events
}

#[test]
fn not_a_pst_and_missing_file() {
    let dir = tempfile::tempdir().unwrap();
    let txt = dir.path().join("notes.pst");
    std::fs::write(&txt, "Just some text pretending to be a PST.\n").unwrap();
    for cmd in ["probe", "list"] {
        let out = run(&[cmd, "--pst", txt.to_str().unwrap()]);
        assert_eq!(out.code, 2);
        let ev = out.events();
        assert_eq!(ev.len(), 1);
        assert_eq!(ev[0]["type"], "fatal");
        assert_eq!(ev[0]["code"], "unsupported");
    }
    let empty = dir.path().join("empty.pst");
    std::fs::write(&empty, b"").unwrap();
    assert_eq!(
        run(&["probe", "--pst", empty.to_str().unwrap()]).events()[0]["code"],
        "unsupported"
    );
    let missing = dir.path().join("missing.pst");
    let out = run(&["list", "--pst", missing.to_str().unwrap()]);
    assert_eq!(out.code, 2);
    assert_eq!(out.events()[0]["code"], "not_found");
    let (out, _) = extract(&missing, "m:00200004", &[]);
    assert_eq!(out.code, 2);
    // A directory is not a PST either.
    let out = run(&["probe", "--pst", dir.path().to_str().unwrap()]);
    assert_eq!(out.code, 2);
}

#[test]
fn truncated_pst_files() {
    let data = std::fs::read(fixture("basic.pst")).unwrap();
    let dir = tempfile::tempdir().unwrap();
    let n = data.len();
    for len in [
        3,
        12,
        100,
        511,
        600,
        4096,
        0x4400,
        0x10000,
        n / 3,
        n / 2,
        n - 4096,
        n - 1,
    ] {
        let p = dir.path().join(format!("trunc-{len}.pst"));
        std::fs::write(&p, &data[..len]).unwrap();
        let events = assert_graceful(&p);
        if len < 564 {
            assert_eq!(events.last().unwrap()["type"], "fatal");
        }
    }
}

#[test]
fn corrupted_byte_ranges_never_crash() {
    let data = std::fs::read(fixture("basic.pst")).unwrap();
    let dir = tempfile::tempdir().unwrap();
    let n = data.len();
    let mut cases = 0;
    let mut degraded = 0;
    for (fi, fill) in [0x00u8, 0xFF, 0x5A].into_iter().enumerate() {
        for k in 0..48 {
            // Spread offsets over the whole file, header included.
            let off = (k * (n - 256) / 48 + fi * 97) % (n - 256);
            let mut bytes = data.clone();
            for b in &mut bytes[off..off + 200] {
                *b = fill ^ (*b & 0x0F);
            }
            let p = dir.path().join(format!("corrupt-{fill:02x}-{off}.pst"));
            std::fs::write(&p, &bytes).unwrap();
            let events = assert_graceful(&p);
            cases += 1;
            if events
                .iter()
                .any(|e| e["type"] == "error" || e["type"] == "fatal")
            {
                degraded += 1;
            }
            std::fs::remove_file(&p).unwrap();
        }
    }
    assert_eq!(cases, 144);
    // Not a tautology: some corruptions must actually be detected.
    assert!(degraded > 0);
}

/// MS-PST CRC (CRC-32, reflected 0xEDB88320, no pre/post inversion).
fn pst_crc(data: &[u8]) -> u32 {
    let mut crc = 0u32;
    for &b in data {
        crc ^= b as u32;
        for _ in 0..8 {
            crc = if crc & 1 != 0 {
                (crc >> 1) ^ 0xEDB8_8320
            } else {
                crc >> 1
            };
        }
    }
    crc
}

#[test]
fn node_btree_cycle_is_detected_not_followed() {
    // Rewrite the node B-tree root page as an intermediate page whose only
    // entry points back at itself (with a valid page CRC). The library would
    // recurse forever on lookups; the engine must reject the file.
    let mut data = std::fs::read(fixture("basic.pst")).unwrap();
    let rd64 = |d: &[u8], o: usize| u64::from_le_bytes(d[o..o + 8].try_into().unwrap());
    // Unicode HEADER.root.BREFNBT = (bid, ib) at 0xD8.
    let nbt_bid = rd64(&data, 0xD8);
    let nbt_ib = rd64(&data, 0xE0) as usize;
    assert!(nbt_ib > 0 && nbt_ib + 512 <= data.len());
    let page = &mut data[nbt_ib..nbt_ib + 512];
    assert_eq!(page[496], 0x81, "NBT page type");
    for b in &mut page[..488] {
        *b = 0;
    }
    page[0..8].copy_from_slice(&0u64.to_le_bytes()); // btkey
    page[8..16].copy_from_slice(&nbt_bid.to_le_bytes()); // BREF.bid
    page[16..24].copy_from_slice(&(nbt_ib as u64).to_le_bytes()); // BREF.ib
    page[488] = 1; // cEnt
    page[489] = 20; // cEntMax
    page[490] = 24; // cbEnt
    page[491] = 1; // cLevel
    let crc = pst_crc(&page[..496]);
    page[500..504].copy_from_slice(&crc.to_le_bytes());
    let dir = tempfile::tempdir().unwrap();
    let p = dir.path().join("cycle.pst");
    std::fs::write(&p, &data).unwrap();
    for cmd in ["probe", "list"] {
        let out = run_full(
            &[cmd, "--pst", p.to_str().unwrap()],
            None,
            &[],
            Duration::from_secs(10),
        );
        assert_eq!(out.code, 2, "{cmd}: {}", out.stdout);
        let ev = out.events();
        assert_eq!(ev[0]["type"], "fatal");
        assert_eq!(ev[0]["code"], "corrupt");
        assert!(
            ev[0]["message"].as_str().unwrap().contains("node B-tree"),
            "{}",
            ev[0]
        );
    }
}

#[test]
fn pst_is_never_modified() {
    let dir = tempfile::tempdir().unwrap();
    for name in ["basic.pst", "empty-outlook-permute.pst"] {
        let pst = copy_fixture(dir.path(), name);
        let before = snapshot(&pst);
        let p = pst.to_str().unwrap();
        assert_eq!(run(&["probe", "--pst", p]).code, 0);
        assert_eq!(snapshot(&pst), before, "probe");
        assert_eq!(run(&["list", "--pst", p]).code, 0);
        assert_eq!(snapshot(&pst), before, "list");
        for id in message_ids(&pst) {
            let (out, _) = extract(&pst, &id, &[]);
            assert_eq!(out.code, 0);
            assert_eq!(snapshot(&pst), before, "extract {id}");
        }
        let work = tempfile::tempdir().unwrap();
        let input: String = message_ids(&pst)
            .iter()
            .enumerate()
            .map(|(i, m)| {
                format!(
                    "{}\n",
                    json!({"op":"extract","requestId":format!("s{i}"),"messageId":m})
                )
            })
            .collect();
        let out = run_with_input(
            &[
                "serve",
                "--pst",
                p,
                "--work-dir",
                work.path().to_str().unwrap(),
            ],
            Some(&input),
        );
        assert_eq!(out.code, 0);
        assert_eq!(snapshot(&pst), before, "serve");
    }
}

#[cfg(target_os = "linux")]
#[test]
fn pst_is_opened_read_only() {
    // While `serve` is running, every descriptor it holds on the PST must be
    // O_RDONLY (checked via /proc/<pid>/fdinfo).
    let dir = tempfile::tempdir().unwrap();
    let pst = copy_fixture(dir.path(), "basic.pst");
    let canonical = std::fs::canonicalize(&pst).unwrap();
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
    writeln!(
        stdin,
        "{}",
        json!({"op":"extract","requestId":"r0","messageId":ids[1]})
    )
    .unwrap();
    stdin.flush().unwrap();
    let mut line = String::new();
    stdout.read_line(&mut line).unwrap();
    assert!(line.contains("\"extracted\""));

    let pid = child.id();
    let mut pst_fds = 0;
    for entry in std::fs::read_dir(format!("/proc/{pid}/fd")).unwrap() {
        let entry = entry.unwrap();
        let Ok(target) = std::fs::read_link(entry.path()) else {
            continue;
        };
        if target != canonical {
            continue;
        }
        pst_fds += 1;
        let fd = entry.file_name().into_string().unwrap();
        let info = std::fs::read_to_string(format!("/proc/{pid}/fdinfo/{fd}")).unwrap();
        let flags = info
            .lines()
            .find_map(|l| l.strip_prefix("flags:"))
            .map(|v| u32::from_str_radix(v.trim(), 8).unwrap())
            .unwrap();
        assert_eq!(
            flags & 0o3,
            0,
            "fd {fd} on the PST is not O_RDONLY (flags {flags:o})"
        );
    }
    assert!(pst_fds >= 1, "expected open descriptors on the PST");
    drop(stdin);
    assert!(child.wait().unwrap().success());
}

#[test]
fn usage_errors_exit_3() {
    let pst = fixture("basic.pst");
    let p = pst.to_str().unwrap();
    for args in [
        vec![],
        vec!["frobnicate", "--pst", p],
        vec!["list"],
        vec!["list", "--pst"],
        vec!["list", "--pst", p, "--bogus", "1"],
        vec!["list", "--pst", p, "--max-body-bytes", "-1"],
        vec!["list", "--pst", p, "--max-body-bytes", "lots"],
        vec!["serve", "--pst", p],
        vec!["extract", "--pst", p, "--message", "m:00200004"],
        vec!["list", "--pst", p, "--pst", p],
    ] {
        let out = run(&args);
        assert_eq!(out.code, 3, "{args:?}");
        assert!(
            out.stdout.is_empty(),
            "usage errors print nothing on stdout"
        );
    }
    let out = run(&["--help"]);
    assert_eq!(out.code, 0);
    let out = run(&["--version"]);
    assert_eq!(out.code, 0);
    assert!(out.stdout.contains("nubarca-pst/1"));
}

#[test]
fn folder_depth_limit_is_classified() {
    let pst = fixture("basic.pst");
    let out = run(&[
        "list",
        "--pst",
        pst.to_str().unwrap(),
        "--max-folder-depth",
        "2",
    ]);
    assert_eq!(out.code, 0);
    let ev = out.events();
    let deep: Vec<_> = ev
        .iter()
        .filter(|e| e["type"] == "error" && e["scope"] == "folder")
        .collect();
    assert_eq!(deep.len(), 1, "Projects/2026/Deep is at depth 3");
    assert_eq!(deep[0]["code"], "limit_exceeded");
    assert!(ev
        .iter()
        .all(|e| e["type"] != "folder" || e["depth"].as_u64().unwrap() <= 2));
    let end = ev.last().unwrap();
    assert_eq!(end["errors"], 1);
    assert_eq!(end["messages"], 4);
}

#[test]
fn diagnostics_never_contain_message_content() {
    // Run everything with corrupted input and make sure no subject, address
    // or body text from the fixture leaks into stderr or error messages.
    let data = std::fs::read(fixture("basic.pst")).unwrap();
    let dir = tempfile::tempdir().unwrap();
    let mut bytes = data.clone();
    let n = bytes.len();
    for b in &mut bytes[n / 2..n / 2 + 4000] {
        *b = 0xA5;
    }
    let p = dir.path().join("c.pst");
    std::fs::write(&p, &bytes).unwrap();
    let mut texts = Vec::new();
    for cmd in ["probe", "list"] {
        let out = run(&[cmd, "--pst", p.to_str().unwrap()]);
        texts.push(out.stderr.clone());
        for e in out.events() {
            if e["type"] == "error" || e["type"] == "fatal" {
                texts.push(e["message"].as_str().unwrap().to_string());
            }
        }
    }
    for t in texts {
        for needle in ["example.test", "Ada", "Bob", "Perché", "notes.txt", "Hello"] {
            assert!(!t.contains(needle), "diagnostic leaked {needle:?}: {t}");
        }
    }
}
