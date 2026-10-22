//! probe / list / extract against the committed fixtures.

mod common;

use std::collections::HashMap;

use common::*;
use nubarca_pst_fixture_gen as gen;
use serde_json::Value;

fn basic() -> std::path::PathBuf {
    fixture("basic.pst")
}

#[test]
fn probe_reports_format_encryption_and_store() {
    let out = run(&["probe", "--pst", basic().to_str().unwrap()]);
    assert_eq!(out.code, 0, "{}", out.stderr);
    let ev = out.events();
    assert_eq!(ev.len(), 1);
    let p = &ev[0];
    assert_eq!(p["type"], "probe");
    assert_eq!(p["protocol"], "nubarca-pst/1");
    assert_eq!(p["format"], "unicode");
    assert_eq!(p["encryption"], "none");
    assert_eq!(p["storeDisplayName"], gen::STORE_NAME);
    assert_eq!(p["fileSize"], std::fs::metadata(basic()).unwrap().len());
}

#[test]
fn probe_and_list_outlook_created_permute_encoded_pst() {
    // examples/Empty.pst of outlook-pst 1.2.0: written by Outlook, permute-encoded.
    let pst = fixture("empty-outlook-permute.pst");
    let out = run(&["probe", "--pst", pst.to_str().unwrap()]);
    assert_eq!(out.code, 0, "{}", out.stderr);
    let p = &out.events()[0];
    assert_eq!(p["format"], "unicode");
    assert_eq!(p["encryption"], "permute");
    assert_eq!(p["storeDisplayName"], "Empty");

    let out = run(&["list", "--pst", pst.to_str().unwrap()]);
    assert_eq!(out.code, 0, "{}", out.stderr);
    let ev = out.events();
    let names: Vec<_> = ev
        .iter()
        .filter(|e| e["type"] == "folder")
        .map(|e| e["name"].as_str().unwrap().to_string())
        .collect();
    assert_eq!(names, ["Top of Outlook data file", "Deleted Items"]);
    assert_eq!(ev.last().unwrap()["type"], "end");
}

#[test]
fn list_streams_folders_parents_first_with_messages() {
    let out = run(&["list", "--pst", basic().to_str().unwrap()]);
    assert_eq!(out.code, 0, "{}", out.stderr);
    let ev = out.events();
    let mut seen_folders: HashMap<String, Value> = HashMap::new();
    let mut messages_per_folder: HashMap<Vec<String>, usize> = HashMap::new();
    let mut message_ids = Vec::new();
    for e in &ev {
        match e["type"].as_str().unwrap() {
            "folder" => {
                let id = e["folderId"].as_str().unwrap();
                assert!(id.len() == 10 && id.starts_with("f:"), "{id}");
                match e["parentFolderId"].as_str() {
                    None => assert_eq!(e["depth"], 0),
                    Some(parent) => {
                        let p = seen_folders
                            .get(parent)
                            .expect("parent listed before child");
                        assert_eq!(
                            e["depth"].as_u64().unwrap(),
                            p["depth"].as_u64().unwrap() + 1
                        );
                        let mut expected = p["path"].as_array().unwrap().clone();
                        expected.push(e["name"].clone());
                        assert_eq!(e["path"].as_array().unwrap(), &expected);
                    }
                }
                seen_folders.insert(id.to_string(), e.clone());
            }
            "message" => {
                let id = e["messageId"].as_str().unwrap();
                assert!(id.len() == 10 && id.starts_with("m:"), "{id}");
                let f = seen_folders
                    .get(e["folderId"].as_str().unwrap())
                    .expect("folder first");
                let path: Vec<String> = f["path"].as_array().unwrap()[1..]
                    .iter()
                    .map(|v| v.as_str().unwrap().to_string())
                    .collect();
                *messages_per_folder.entry(path).or_default() += 1;
                message_ids.push(id.to_string());
            }
            "end" => {}
            other => panic!("unexpected event {other}: {e}"),
        }
    }
    let end = ev.last().unwrap();
    assert_eq!(end["type"], "end");
    assert_eq!(end["errors"], 0);
    assert_eq!(end["messages"], 5);
    assert_eq!(
        end["folders"].as_u64().unwrap() as usize,
        seen_folders.len()
    );
    // Nested path Projects/2026/Deep/Nested exists with its message.
    let mut expected: HashMap<Vec<String>, usize> = HashMap::new();
    for m in gen::basic_messages() {
        *expected.entry(m.folder.clone()).or_default() += 1;
    }
    assert_eq!(messages_per_folder, expected);
    // Contents count matches the folder property.
    for f in seen_folders.values() {
        if let Some(n) = f["contentCount"].as_u64() {
            let msgs = ev
                .iter()
                .filter(|e| e["type"] == "message" && e["folderId"] == f["folderId"])
                .count();
            assert_eq!(n as usize, msgs, "contentCount of {}", f["name"]);
        }
    }
    let unique: std::collections::HashSet<_> = message_ids.iter().collect();
    assert_eq!(unique.len(), message_ids.len());
}

/// Extract every message and compare with the generator's specification.
#[test]
fn extract_matches_generator_specification() {
    let pst = basic();
    let ids = message_ids(&pst);
    let spec = gen::basic_messages();
    assert_eq!(ids.len(), spec.len());
    let mut by_subject: HashMap<String, gen::Message> =
        spec.into_iter().map(|m| (m.subject.clone(), m)).collect();
    for id in &ids {
        let (out, dir) = extract(&pst, id, &[]);
        assert_eq!(out.code, 0, "{}", out.stdout);
        let resp = &out.events()[0];
        assert_eq!(resp["type"], "extracted");
        assert_eq!(resp["messageId"], id.as_str());
        assert_eq!(resp["document"], "message.json");
        let doc = read_doc(&dir);
        assert_eq!(
            resp["documentBytes"].as_u64().unwrap(),
            std::fs::metadata(dir.path().join("out/message.json"))
                .unwrap()
                .len()
        );
        assert_eq!(doc["format"], "nubarca-mail-message");
        assert_eq!(doc["version"], 1);
        assert_eq!(doc["source"]["messageId"], id.as_str());
        assert!(doc["source"]["folderId"]
            .as_str()
            .unwrap()
            .starts_with("f:"));
        let subject = doc["subject"].as_str().unwrap().to_string();
        let m = by_subject
            .remove(&subject)
            .unwrap_or_else(|| panic!("unexpected subject {subject}"));

        assert_eq!(doc["messageClass"], "IPM.Note");
        assert_eq!(doc["conversationTopic"], m.subject.as_str());
        assert_eq!(doc["internetMessageId"], m.message_id.as_str());
        assert_eq!(doc["from"]["name"], m.sender_name.as_str());
        assert_eq!(doc["from"]["address"], m.sender_email.as_str());
        assert!(doc["sender"].is_null());
        assert_eq!(doc["receivedAt"], props_iso(m.delivery_time));
        assert_eq!(doc["sentAt"], props_iso(m.delivery_time));
        assert_eq!(doc["body"]["text"], m.body.as_str());
        match &m.html {
            Some(h) => assert_eq!(doc["body"]["html"], h.as_str()),
            None => assert!(doc["body"]["html"].is_null()),
        }
        assert!(doc["body"]["rtf"].is_null());
        assert_eq!(doc["body"]["truncated"]["text"], false);

        for (field, kind) in [("to", 1u8), ("cc", 2), ("bcc", 3)] {
            let expected: Vec<(String, String)> = m
                .recipients
                .iter()
                .filter(|r| r.kind == kind)
                .map(|r| (r.name.clone(), r.email.clone()))
                .collect();
            let actual: Vec<(String, String)> = doc[field]
                .as_array()
                .unwrap()
                .iter()
                .map(|a| {
                    (
                        a["name"].as_str().unwrap().into(),
                        a["address"].as_str().unwrap().into(),
                    )
                })
                .collect();
            assert_eq!(actual, expected, "{field} of {subject}");
        }

        let atts = doc["attachments"].as_array().unwrap();
        assert_eq!(atts.len(), m.attachments.len());
        assert_eq!(doc["hasAttachments"], !m.attachments.is_empty());
        let files = resp["attachments"].as_array().unwrap();
        assert_eq!(files.len(), m.attachments.len());
        for (i, (a, spec_a)) in atts.iter().zip(&m.attachments).enumerate() {
            assert_eq!(a["key"], i.to_string());
            assert_eq!(a["method"], "by_value");
            assert_eq!(a["fileName"], spec_a.filename.as_str());
            assert_eq!(a["mimeType"], spec_a.mime.as_str());
            assert_eq!(a["extracted"], true);
            assert!(a["skipReason"].is_null());
            assert_eq!(a["sizeBytes"].as_u64().unwrap() as usize, spec_a.data.len());
            let cid = spec_a
                .content_id
                .as_ref()
                .map(|c| c.trim_matches(|ch| ch == '<' || ch == '>'));
            assert_eq!(a["contentId"].as_str(), cid);
            assert_eq!(a["inline"], cid.is_some());
            assert_eq!(a["hidden"], false);
            // The response lists the file with the bytes the generator wrote.
            let f = &files[i];
            assert_eq!(f["key"], i.to_string());
            assert_eq!(f["file"], format!("att-{i}.bin"));
            assert_eq!(f["sha256"], gen::sha256_hex(&spec_a.data));
            assert_eq!(f["sizeBytes"].as_u64().unwrap() as usize, spec_a.data.len());
            let on_disk = dir.path().join("out").join(f["file"].as_str().unwrap());
            assert_eq!(std::fs::read(&on_disk).unwrap(), spec_a.data);
        }
    }
    assert!(by_subject.is_empty());
}

fn props_iso(ticks: i64) -> String {
    let unix = ticks / 10_000_000 - 11_644_473_600;
    // Only whole hours from T0 are used by the fixtures.
    let base = 1_709_287_200i64; // 2024-03-01T10:00:00Z
    let hours = (unix - base) / 3600;
    format!("2024-03-01T{:02}:00:00Z", 10 + hours)
}

#[test]
fn duplicate_names_inline_cid_and_unicode() {
    let pst = basic();
    let ids = message_ids(&pst);
    let mut seen_dup = false;
    let mut seen_cid = false;
    let mut seen_intl = false;
    for id in &ids {
        let (out, dir) = extract(&pst, id, &[]);
        assert_eq!(out.code, 0);
        let doc = read_doc(&dir);
        let atts = doc["attachments"].as_array().unwrap();
        let names: Vec<_> = atts
            .iter()
            .map(|a| a["fileName"].as_str().unwrap())
            .collect();
        if names.iter().filter(|n| **n == "notes.txt").count() == 2 {
            seen_dup = true;
            // Same name, distinct keys and files, different bytes.
            let resp = &out.events()[0];
            let f0 = &resp["attachments"][0];
            let f1 = &resp["attachments"][1];
            assert_ne!(f0["file"], f1["file"]);
            assert_ne!(f0["sha256"], f1["sha256"]);
            // The zero-byte attachment is still extracted (empty file).
            let empty = atts
                .iter()
                .position(|a| a["fileName"] == "empty.txt")
                .unwrap();
            assert_eq!(atts[empty]["extracted"], true);
            assert_eq!(resp["attachments"][empty]["sizeBytes"], 0);
            assert_eq!(
                resp["attachments"][empty]["sha256"],
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
            );
        }
        if let Some(html) = doc["body"]["html"].as_str() {
            for a in atts {
                if let Some(cid) = a["contentId"].as_str() {
                    assert!(html.contains(&format!("cid:{cid}")), "html references cid");
                    assert_eq!(a["inline"], true);
                    seen_cid = true;
                }
            }
        }
        if doc["subject"] == gen::INTL_SUBJECT {
            seen_intl = true;
            assert!(doc["body"]["text"]
                .as_str()
                .unwrap()
                .contains("À bientôt! ½ € ß ñ 😀"));
            assert_eq!(atts[0]["fileName"], gen::INTL_FILENAME);
            assert_eq!(doc["from"]["name"], "Zoë Ünlü");
            assert_eq!(doc["to"][0]["name"], "Γιώργος Παπαδόπουλος");
            assert_eq!(doc["cc"][0]["name"], "王小明");
        }
    }
    assert!(seen_dup && seen_cid && seen_intl);
}

#[test]
fn reopen_specific_message_by_id_in_any_order() {
    let pst = basic();
    let mut ids = message_ids(&pst);
    ids.reverse();
    let mut subjects = Vec::new();
    for id in &ids {
        let (out, dir) = extract(&pst, id, &[]);
        assert_eq!(out.code, 0);
        subjects.push(read_doc(&dir)["subject"].as_str().unwrap().to_string());
    }
    // Opening the same id twice gives the same document.
    let (_, d1) = extract(&pst, &ids[0], &[]);
    let (_, d2) = extract(&pst, &ids[0], &[]);
    assert_eq!(
        std::fs::read(d1.path().join("out/message.json")).unwrap(),
        std::fs::read(d2.path().join("out/message.json")).unwrap()
    );
    assert_eq!(subjects.len(), 5);
}

#[test]
fn extract_unknown_and_non_message_ids() {
    let pst = basic();
    for id in ["m:00ffff04", "m:000080a2", "m:00000000"] {
        let (out, _) = extract(&pst, id, &[]);
        assert_eq!(out.code, 2, "{id}: {}", out.stdout);
        let ev = out.events();
        assert_eq!(ev[0]["type"], "fatal");
        assert_eq!(ev[0]["code"], "not_found", "{id}");
    }
    let (out, _) = extract(&pst, "m:XYZ", &[]);
    assert_eq!(out.code, 2);
    assert_eq!(out.events()[0]["code"], "bad_request");
}

#[test]
fn limits_body_and_attachment_count() {
    let pst = basic();
    let ids = message_ids(&pst);
    let mut checked = 0;
    for id in &ids {
        let (out, dir) = extract(
            &pst,
            id,
            &[
                "--max-body-bytes",
                "10",
                "--max-attachments-per-message",
                "1",
                "--max-attachment-bytes",
                "1000",
            ],
        );
        assert_eq!(out.code, 0);
        let doc = read_doc(&dir);
        let resp = &out.events()[0];
        // Every fixture body is longer than 10 bytes.
        let t = doc["body"]["text"].as_str().unwrap();
        assert!(t.len() <= 10, "{t:?}");
        assert_eq!(doc["body"]["truncated"]["text"], true);
        assert!(resp["warnings"]
            .as_array()
            .unwrap()
            .iter()
            .any(|w| w["code"] == "body_truncated"));
        if let Some(h) = doc["body"]["html"].as_str() {
            assert!(h.len() <= 10);
            assert_eq!(doc["body"]["truncated"]["html"], true);
        }
        let atts = doc["attachments"].as_array().unwrap();
        if atts.len() == 4 {
            checked += 1;
            // First within limits; the rest are described, not extracted.
            assert_eq!(atts[0]["extracted"], true);
            for a in &atts[1..] {
                assert_eq!(a["extracted"], false);
                assert_eq!(a["skipReason"], "limit_exceeded");
            }
            assert_eq!(resp["attachments"].as_array().unwrap().len(), 1);
            assert!(resp["warnings"]
                .as_array()
                .unwrap()
                .iter()
                .any(|w| w["code"] == "attachments_limit_exceeded"));
        }
    }
    assert_eq!(checked, 1);
}

#[test]
fn too_large_attachment_has_skip_reason_and_no_file() {
    let pst = basic();
    let ids = message_ids(&pst);
    let mut found = false;
    for id in &ids {
        let (out, dir) = extract(&pst, id, &["--max-attachment-bytes", "50000"]);
        assert_eq!(out.code, 0);
        let doc = read_doc(&dir);
        for (i, a) in doc["attachments"].as_array().unwrap().iter().enumerate() {
            if a["fileName"] == "data.bin" {
                found = true;
                assert_eq!(a["extracted"], false);
                assert_eq!(a["skipReason"], "too_large");
                assert_eq!(a["sizeBytes"], 100_000);
                assert!(!dir.path().join(format!("out/att-{i}.bin")).exists());
                let resp = &out.events()[0];
                assert!(resp["attachments"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .all(|f| f["key"].as_str() != Some(i.to_string().as_str())));
            }
        }
    }
    assert!(found);
}

#[test]
fn document_contains_no_attachment_bytes() {
    let pst = basic();
    for id in message_ids(&pst) {
        let (_, dir) = extract(&pst, &id, &[]);
        let raw = std::fs::read_to_string(dir.path().join("out/message.json")).unwrap();
        // The 100 kB payload would show up as a long base64 or hex run.
        assert!(
            raw.len() < 16 * 1024,
            "document unexpectedly large: {}",
            raw.len()
        );
    }
}

#[test]
fn permute_and_cyclic_encoded_copies_decode_identically() {
    let pst = basic();
    let ids = message_ids(&pst);
    let dir = tempfile::tempdir().unwrap();
    for (enc, name) in [
        (gen::Encoding::Permute, "permute"),
        (gen::Encoding::Cyclic, "cyclic"),
    ] {
        let encoded = dir.path().join(format!("basic-{name}.pst"));
        gen::encode_pst(&pst, &encoded, enc).unwrap();
        assert_ne!(
            std::fs::read(&encoded).unwrap(),
            std::fs::read(&pst).unwrap()
        );
        let out = run(&["probe", "--pst", encoded.to_str().unwrap()]);
        assert_eq!(out.code, 0, "{}", out.stdout);
        assert_eq!(out.events()[0]["encryption"], name);
        assert_eq!(out.events()[0]["storeDisplayName"], gen::STORE_NAME);
        assert_eq!(message_ids(&encoded), ids);
        for id in &ids {
            let (plain, plain_dir) = extract(&pst, id, &[]);
            let (coded, coded_dir) = extract(&encoded, id, &[]);
            assert_eq!(coded.code, 0, "{name} {id}: {}", coded.stdout);
            assert_eq!(read_doc(&plain_dir), read_doc(&coded_dir), "{name} {id}");
            assert_eq!(
                plain.events()[0]["attachments"],
                coded.events()[0]["attachments"]
            );
        }
    }
}
