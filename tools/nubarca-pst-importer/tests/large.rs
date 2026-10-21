//! Large attachments are streamed: peak memory must not grow with the
//! attachment size. The PST is generated at test time (not committed).
//!
//! Size defaults to 64 MiB; override with NUBARCA_PST_LARGE_BYTES, e.g.
//! `NUBARCA_PST_LARGE_BYTES=268435456 cargo test --release --test large -- --nocapture`.

mod common;

use std::time::{Duration, Instant};

use common::*;

const PEAK_RSS_BUDGET_KIB: u64 = 48 * 1024;

#[test]
fn large_attachment_is_streamed_with_bounded_memory() {
    let size: usize = std::env::var("NUBARCA_PST_LARGE_BYTES")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(64 << 20);
    let dir = tempfile::tempdir().unwrap();
    let pst = dir.path().join("large.pst");
    let sha = nubarca_pst_fixture_gen::write_large_attachment(&pst, size).unwrap();
    let ids = message_ids(&pst);
    assert_eq!(ids.len(), 1);
    let p = pst.to_str().unwrap();
    let env = [("NUBARCA_PST_REPORT_PEAK_RSS", "1")];

    // Full extraction.
    let out_dir = dir.path().join("out");
    let started = Instant::now();
    let out = run_full(
        &[
            "extract",
            "--pst",
            p,
            "--message",
            &ids[0],
            "--out-dir",
            out_dir.to_str().unwrap(),
        ],
        None,
        &env,
        Duration::from_secs(600),
    );
    let elapsed = started.elapsed();
    assert_eq!(out.code, 0, "{}", out.stdout);
    let resp = &out.events()[0];
    let f = &resp["attachments"][0];
    assert_eq!(f["sizeBytes"].as_u64().unwrap() as usize, size);
    assert_eq!(f["sha256"], sha.as_str());
    assert_eq!(sha256_file(&out_dir.join("att-0.bin")), sha);
    let rss = peak_rss_kib(&out.stderr).expect("peak rss reported");
    println!(
        "large attachment: {} MiB extracted in {:.2?} ({:.1} MiB/s), peak RSS {} KiB",
        size >> 20,
        elapsed,
        size as f64 / (1 << 20) as f64 / elapsed.as_secs_f64(),
        rss
    );
    assert!(
        rss < PEAK_RSS_BUDGET_KIB,
        "peak RSS {rss} KiB for a {size}-byte attachment"
    );

    // Over the limit: described, not extracted, no file, still bounded.
    let out_dir2 = dir.path().join("out2");
    let limit = (size / 2).to_string();
    let out = run_full(
        &[
            "extract",
            "--pst",
            p,
            "--message",
            &ids[0],
            "--out-dir",
            out_dir2.to_str().unwrap(),
            "--max-attachment-bytes",
            &limit,
        ],
        None,
        &env,
        Duration::from_secs(600),
    );
    assert_eq!(out.code, 0);
    let resp = &out.events()[0];
    assert!(resp["attachments"].as_array().unwrap().is_empty());
    let doc: serde_json::Value =
        serde_json::from_slice(&std::fs::read(out_dir2.join("message.json")).unwrap()).unwrap();
    let a = &doc["attachments"][0];
    assert_eq!(a["extracted"], false);
    assert_eq!(a["skipReason"], "too_large");
    assert_eq!(a["sizeBytes"].as_u64().unwrap() as usize, size);
    assert!(!out_dir2.join("att-0.bin").exists());
    let rss = peak_rss_kib(&out.stderr).unwrap();
    println!("too_large path: peak RSS {rss} KiB");
    assert!(rss < PEAK_RSS_BUDGET_KIB);
}
