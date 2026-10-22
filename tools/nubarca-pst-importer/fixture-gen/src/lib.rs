//! Dev-only generator of synthetic PST fixtures for `nubarca-pst-importer`.
//!
//! Uses the MIT `outlook-pst-rw` fork (renamed `pst_writer`) to *create*
//! Unicode PST files. All content is synthetic: no personal data. The same
//! specification that drives the writer is what the importer tests assert
//! against, so expected values never drift from the files.

use std::io;
use std::path::Path;

use pst_writer::{
    UnicodePstAttachment, UnicodePstBatchMessage, UnicodePstFile, UnicodePstMessage,
    UnicodePstRecipient, UnicodePstRecipientType,
};
use sha2::{Digest, Sha256};

mod encode;
pub use encode::{encode_pst, pst_crc, Encoding};

/// A recipient: kind 1 = To, 2 = Cc, 3 = Bcc.
#[derive(Clone, Debug)]
pub struct Recipient {
    pub name: String,
    pub email: String,
    pub kind: u8,
}

#[derive(Clone, Debug)]
pub struct Attachment {
    pub filename: String,
    pub mime: String,
    pub content_id: Option<String>,
    pub data: Vec<u8>,
}

#[derive(Clone, Debug)]
pub struct Message {
    /// Folder path below the IPM subtree.
    pub folder: Vec<String>,
    pub subject: String,
    pub sender_name: String,
    pub sender_email: String,
    pub recipients: Vec<Recipient>,
    pub body: String,
    pub html: Option<String>,
    pub message_id: String,
    /// FILETIME ticks.
    pub delivery_time: i64,
    pub attachments: Vec<Attachment>,
}

pub const STORE_NAME: &str = "NubArca Fixture Archive";

/// 2024-03-01T10:00:00Z as FILETIME.
pub const T0: i64 = (1_709_287_200 + 11_644_473_600) * 10_000_000;

/// A 1x1 PNG.
pub const INLINE_PNG: &[u8] = &[
    0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
    0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x04, 0x00, 0x00, 0x00, 0xb5, 0x1c, 0x0c,
    0x02, 0x00, 0x00, 0x00, 0x0b, 0x49, 0x44, 0x41, 0x54, 0x78, 0xda, 0x63, 0x64, 0xf8, 0x0f, 0x00,
    0x01, 0x05, 0x01, 0x01, 0x27, 0x18, 0xe3, 0x66, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4e, 0x44,
    0xae, 0x42, 0x60, 0x82,
];

pub const INTL_SUBJECT: &str =
    "Perché così? Grüße aus München — Καλημέρα κόσμε — 你好，世界 — こんにちは 🎉📎";
pub const INTL_FILENAME: &str = "résumé-Übersicht-Ωμέγα-日本語-🎉.txt";

/// Deterministic pseudo-random bytes (xorshift64*), so payload hashes are
/// reproducible without storing payloads.
pub fn pattern(len: usize, seed: u64) -> Vec<u8> {
    let mut x = seed.wrapping_mul(0x9E37_79B9_7F4A_7C15) | 1;
    let mut out = Vec::with_capacity(len);
    while out.len() < len {
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        let v = x.wrapping_mul(0x2545_F491_4F6C_DD1D);
        for b in v.to_le_bytes() {
            if out.len() == len {
                break;
            }
            out.push(b);
        }
    }
    out
}

pub fn sha256_hex(data: &[u8]) -> String {
    Sha256::digest(data)
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
}

fn rcpt(name: &str, email: &str, kind: u8) -> Recipient {
    Recipient {
        name: name.into(),
        email: email.into(),
        kind,
    }
}

fn att(filename: &str, mime: &str, cid: Option<&str>, data: Vec<u8>) -> Attachment {
    Attachment {
        filename: filename.into(),
        mime: mime.into(),
        content_id: cid.map(Into::into),
        data,
    }
}

fn path(parts: &[&str]) -> Vec<String> {
    parts.iter().map(|s| s.to_string()).collect()
}

/// The committed `tests/fixtures/basic.pst`.
pub fn basic_messages() -> Vec<Message> {
    let base = Message {
        folder: path(&["Inbox"]),
        subject: String::new(),
        sender_name: "Ada Example".into(),
        sender_email: "ada@example.test".into(),
        recipients: vec![rcpt("Bob Example", "bob@example.test", 1)],
        body: String::new(),
        html: None,
        message_id: String::new(),
        delivery_time: T0,
        attachments: Vec::new(),
    };
    vec![
        Message {
            subject: "Plain text only".into(),
            body: "Hello Bob,\r\nthis message has no attachments.\r\n".into(),
            message_id: "<plain-1@example.test>".into(),
            ..base.clone()
        },
        Message {
            subject: "HTML with inline image".into(),
            body: "Plain-text alternative of the HTML body.".into(),
            html: Some(
                "<html><body><p>Inline image: <img src=\"cid:logo-1@example.test\"></p>\
                 <p>See the attached PDF.</p></body></html>"
                    .into(),
            ),
            message_id: "<html-1@example.test>".into(),
            delivery_time: T0 + 3_600 * 10_000_000,
            attachments: vec![
                att(
                    "logo.png",
                    "image/png",
                    Some("<logo-1@example.test>"),
                    INLINE_PNG.to_vec(),
                ),
                att("report.pdf", "application/pdf", None, pattern(20_480, 1)),
            ],
            ..base.clone()
        },
        Message {
            folder: path(&["Projects", "2026"]),
            subject: "Several attachments, duplicate names".into(),
            body: "Two files share a name; one file is empty.".into(),
            message_id: "<dups-1@example.test>".into(),
            recipients: vec![
                rcpt("Bob Example", "bob@example.test", 1),
                rcpt("Carol Example", "carol@example.test", 1),
                rcpt("Dave Example", "dave@example.test", 2),
                rcpt("Eve Example", "eve@example.test", 3),
            ],
            attachments: vec![
                att(
                    "notes.txt",
                    "text/plain",
                    None,
                    b"first notes.txt\n".to_vec(),
                ),
                att(
                    "notes.txt",
                    "text/plain",
                    None,
                    b"second notes.txt, different bytes\n".to_vec(),
                ),
                att("empty.txt", "text/plain", None, Vec::new()),
                // Larger than one 8 KiB data block: exercises XBLOCK trees.
                att(
                    "data.bin",
                    "application/octet-stream",
                    None,
                    pattern(100_000, 2),
                ),
            ],
            ..base.clone()
        },
        Message {
            folder: path(&["Projects", "2026", "Deep", "Nested"]),
            subject: INTL_SUBJECT.into(),
            sender_name: "Zoë Ünlü".into(),
            sender_email: "zoe@example.test".into(),
            recipients: vec![
                rcpt("Γιώργος Παπαδόπουλος", "giorgos@example.test", 1),
                rcpt("王小明", "xiaoming@example.test", 2),
            ],
            body: format!("{INTL_SUBJECT}\r\nÀ bientôt! ½ € ß ñ 😀"),
            html: Some(format!("<p>{INTL_SUBJECT}</p><p>À bientôt! ½ € ß ñ 😀</p>")),
            message_id: "<intl-1@example.test>".into(),
            attachments: vec![att(
                INTL_FILENAME,
                "text/plain",
                None,
                "Contenuto: àèéìòù ÄÖÜß Ωμέγα 日本語 🎉\n"
                    .as_bytes()
                    .to_vec(),
            )],
            ..base.clone()
        },
        Message {
            folder: path(&["Archive"]),
            subject: "Recipients To Cc Bcc".into(),
            body: "Recipient table coverage.".into(),
            message_id: "<rcpt-1@example.test>".into(),
            recipients: vec![
                rcpt("To One", "to1@example.test", 1),
                rcpt("To Two", "to2@example.test", 1),
                rcpt("Cc One", "cc1@example.test", 2),
                rcpt("Cc Two", "cc2@example.test", 2),
                rcpt("Bcc One", "bcc1@example.test", 3),
            ],
            ..base
        },
    ]
}

fn to_writer<'a>(
    m: &'a Message,
    recipients: &'a [UnicodePstRecipient<'a>],
) -> UnicodePstMessage<'a> {
    UnicodePstMessage {
        subject: &m.subject,
        sender_name: &m.sender_name,
        sender_email: &m.sender_email,
        recipients,
        body: &m.body,
        html_body: m.html.as_deref(),
        message_id: &m.message_id,
        delivery_time: m.delivery_time,
    }
}

fn writer_recipients(m: &Message) -> Vec<UnicodePstRecipient<'_>> {
    m.recipients
        .iter()
        .map(|r| UnicodePstRecipient {
            name: &r.name,
            email: &r.email,
            recipient_type: match r.kind {
                1 => UnicodePstRecipientType::To,
                2 => UnicodePstRecipientType::Cc,
                _ => UnicodePstRecipientType::Bcc,
            },
        })
        .collect()
}

fn writer_attachments(m: &Message) -> Vec<UnicodePstAttachment<'_>> {
    m.attachments
        .iter()
        .map(|a| UnicodePstAttachment {
            filename: &a.filename,
            mime_type: &a.mime,
            content_id: a.content_id.as_deref(),
            data: &a.data,
        })
        .collect()
}

/// Write `messages` into a new PST at `path` (which must not exist).
/// Consecutive messages in the same folder are appended in one batch.
pub fn write_messages(
    path: &Path,
    messages: &[Message],
    store_name: Option<&str>,
) -> io::Result<()> {
    let (first, rest) = messages
        .split_first()
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "no messages"))?;
    {
        let r = writer_recipients(first);
        let a = writer_attachments(first);
        let folder: Vec<&str> = first.folder.iter().map(String::as_str).collect();
        drop(UnicodePstFile::create_in_folder_with_attachments(
            path,
            &folder,
            &to_writer(first, &r),
            &a,
        )?);
    }
    let mut i = 0;
    while i < rest.len() {
        let folder = &rest[i].folder;
        let mut j = i;
        while j < rest.len() && &rest[j].folder == folder && j - i < 1000 {
            j += 1;
        }
        let group = &rest[i..j];
        let recips: Vec<_> = group.iter().map(writer_recipients).collect();
        let atts: Vec<_> = group.iter().map(writer_attachments).collect();
        let batch: Vec<_> = group
            .iter()
            .zip(&recips)
            .zip(&atts)
            .map(|((m, r), a)| UnicodePstBatchMessage {
                message: to_writer(m, r),
                attachments: a,
            })
            .collect();
        let folder: Vec<&str> = folder.iter().map(String::as_str).collect();
        drop(UnicodePstFile::append_many_in_folder_with_attachments(
            path, &folder, &batch,
        )?);
        i = j;
    }
    if let Some(name) = store_name {
        drop(UnicodePstFile::set_store_display_name(path, name)?);
    }
    Ok(())
}

pub fn write_basic(path: &Path) -> io::Result<()> {
    write_messages(path, &basic_messages(), Some(STORE_NAME))
}

/// One message carrying one by-value attachment of `size` bytes.
/// Returns the attachment's SHA-256.
pub fn write_large_attachment(path: &Path, size: usize) -> io::Result<String> {
    let data = pattern(size, 42);
    let sha = sha256_hex(&data);
    let m = Message {
        folder: vec!["Inbox".into()],
        subject: "Large attachment".into(),
        sender_name: "Ada Example".into(),
        sender_email: "ada@example.test".into(),
        recipients: vec![rcpt("Bob Example", "bob@example.test", 1)],
        body: "A large attachment follows.".into(),
        html: None,
        message_id: "<large-1@example.test>".into(),
        delivery_time: T0,
        attachments: vec![att("large.bin", "application/octet-stream", None, data)],
    };
    write_messages(path, &[m], None)?;
    Ok(sha)
}

/// A volume corpus: `messages` messages spread over `folders` folders (two
/// levels), every `attach_every`-th message with a small attachment.
pub fn write_volume(
    path: &Path,
    messages: usize,
    folders: usize,
    attach_every: usize,
) -> io::Result<()> {
    let mut all = Vec::with_capacity(messages);
    for i in 0..messages {
        let f = i % folders.max(1);
        let folder = vec![format!("Volume {:02}", f / 4), format!("Sub {:02}", f)];
        let attachments = if attach_every > 0 && i % attach_every == 0 {
            vec![att(
                &format!("file-{i}.bin"),
                "application/octet-stream",
                None,
                pattern(4096 + (i % 7) * 1000, i as u64),
            )]
        } else {
            Vec::new()
        };
        all.push(Message {
            folder,
            subject: format!("Volume message {i}"),
            sender_name: "Ada Example".into(),
            sender_email: "ada@example.test".into(),
            recipients: vec![rcpt("Bob Example", "bob@example.test", 1)],
            body: format!("Body of volume message {i}. ").repeat(8),
            html: if i % 3 == 0 {
                Some(format!("<p>HTML body of volume message {i}</p>"))
            } else {
                None
            },
            message_id: format!("<vol-{i}@example.test>"),
            delivery_time: T0 + i as i64 * 10_000_000,
            attachments,
        });
    }
    // Group by folder so batches are large.
    all.sort_by(|a, b| a.folder.cmp(&b.folder));
    write_messages(path, &all, Some("Volume corpus"))
}
