//! Volume corpus generated at test time: list and extract throughput and
//! peak memory. Ignored by default (it writes ~60 MB and runs ~20k
//! extractions); run with
//!
//! `cargo test --release --test volume -- --ignored --nocapture`
//!
//! NUBARCA_PST_VOLUME_MESSAGES overrides the message count (default 20000).

mod common;

use std::time::{Duration, Instant};

use common::*;
use serde_json::json;

#[test]
#[ignore = "volume measurement; run explicitly"]
fn volume_corpus_throughput() {
    let n: usize = std::env::var("NUBARCA_PST_VOLUME_MESSAGES")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(20_000);
    let dir = tempfile::tempdir().unwrap();
    let pst = dir.path().join("volume.pst");
    let t = Instant::now();
    nubarca_pst_fixture_gen::write_volume(&pst, n, 40, 10).unwrap();
    let pst_bytes = std::fs::metadata(&pst).unwrap().len();
    println!(
        "generated {n} messages, {:.1} MiB PST in {:.2?}",
        pst_bytes as f64 / 1048576.0,
        t.elapsed()
    );
    let p = pst.to_str().unwrap();
    let env = [("NUBARCA_PST_REPORT_PEAK_RSS", "1")];
    let long = Duration::from_secs(1800);

    let t = Instant::now();
    let list = run_full(&["list", "--pst", p], None, &env, long);
    let list_time = t.elapsed();
    assert_eq!(list.code, 0);
    let events = list.events();
    let end = events.last().unwrap();
    assert_eq!(end["messages"].as_u64().unwrap() as usize, n);
    assert_eq!(end["errors"], 0);
    let ids: Vec<String> = events
        .iter()
        .filter(|e| e["type"] == "message")
        .map(|e| e["messageId"].as_str().unwrap().to_string())
        .collect();
    let list_rss = peak_rss_kib(&list.stderr).unwrap();
    println!(
        "list: {n} messages, {} folders in {list_time:.2?} ({:.0} msg/s), peak RSS {list_rss} KiB",
        end["folders"],
        n as f64 / list_time.as_secs_f64()
    );

    let work = tempfile::tempdir().unwrap();
    let input: String = ids
        .iter()
        .enumerate()
        .map(|(i, m)| {
            format!(
                "{}\n",
                json!({"op":"extract","requestId":format!("v{i}"),"messageId":m})
            )
        })
        .collect();
    let t = Instant::now();
    let serve = run_full(
        &[
            "serve",
            "--pst",
            p,
            "--work-dir",
            work.path().to_str().unwrap(),
        ],
        Some(&input),
        &env,
        long,
    );
    let serve_time = t.elapsed();
    assert_eq!(serve.code, 0);
    let resp = serve.events();
    assert_eq!(resp.len(), n);
    let mut doc_bytes = 0u64;
    let mut att_bytes = 0u64;
    let mut att_count = 0usize;
    for r in &resp {
        assert_eq!(r["type"], "extracted", "{r}");
        doc_bytes += r["documentBytes"].as_u64().unwrap();
        for f in r["attachments"].as_array().unwrap() {
            att_bytes += f["sizeBytes"].as_u64().unwrap();
            att_count += 1;
        }
    }
    let serve_rss = peak_rss_kib(&serve.stderr).unwrap();
    let total = (doc_bytes + att_bytes) as f64 / 1048576.0;
    println!(
        "serve/extract: {n} messages in {serve_time:.2?} ({:.0} msg/s), {att_count} attachments, \
         {total:.1} MiB written ({:.1} MiB/s), peak RSS {serve_rss} KiB",
        n as f64 / serve_time.as_secs_f64(),
        total / serve_time.as_secs_f64()
    );
    assert_eq!(att_count, n.div_ceil(10));
}
