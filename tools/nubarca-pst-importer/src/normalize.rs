//! Builds the normalized `nubarca-mail-message` v1 document from a
//! [`MessageObject`] and writes by-value attachments to files. Format
//! independent: unit tests drive it with in-memory fakes.

use std::fs::{self, File, OpenOptions};
use std::io::{self, BufWriter, Write};
use std::path::{Path, PathBuf};

use sha2::{Digest, Sha256};

use crate::engine::{AttachmentObject, MessageObject, RawProp};
use crate::error::{guard, Code, ItemError, ItemResult};
use crate::limits::Limits;
use crate::model::{
    Address, AttachmentDoc, Body, ExtractedFile, MessageContent, MessageDocument, Source,
    Truncated, Warning, DOCUMENT_FORMAT, DOCUMENT_VERSION,
};
use crate::props::{
    decode_codepage, filetime_to_iso, id, known_codepage, truncate_utf8, utf16le_to_string,
    PropMap, PropValue, PT_BINARY, PT_STRING8, PT_UNICODE,
};

/// Transport headers are capped at 256 KiB (contract mandate).
pub const MAX_TRANSPORT_HEADERS: usize = 256 * 1024;
/// Plain string properties (subject, names...) are capped at 64 KiB.
const MAX_SHORT_STRING: usize = 64 * 1024;
/// Recipients described per message.
pub const MAX_RECIPIENTS: usize = 10_000;
/// Beyond `--max-attachments-per-message`, attachments are still described
/// (not extracted) up to this many times the limit.
const DESCRIBE_FACTOR: usize = 4;
/// Hard ceiling on `--max-embedded-depth`, whatever the caller passes.
pub const HARD_MAX_EMBEDDED_DEPTH: u32 = 16;
/// Hard ceiling on attachment descriptors in one document (all levels).
const MAX_TOTAL_ATTACHMENTS: usize = 20_000;

const MSGFLAG_HASATTACH: i64 = 0x10;
const ATT_MHTML_REF: i64 = 0x4;

/// Result of one extraction.
pub struct Extraction {
    pub document_bytes: Vec<u8>,
    pub files: Vec<ExtractedFile>,
    pub warnings: Vec<Warning>,
}

struct Ctx<'a> {
    limits: &'a Limits,
    out_dir: &'a Path,
    /// Prefix for file paths reported in the response (e.g. "r42/").
    report_prefix: &'a str,
    files: Vec<ExtractedFile>,
    written: Vec<PathBuf>,
    total_attachments: usize,
}

/// Extract one message: write attachments and `message.json` into `out_dir`.
/// On failure every file written by this call is removed again.
pub fn extract_message(
    msg: &dyn MessageObject,
    source: Source,
    limits: &Limits,
    out_dir: &Path,
    report_prefix: &str,
) -> ItemResult<Extraction> {
    let mut ctx = Ctx {
        limits,
        out_dir,
        report_prefix,
        files: Vec::new(),
        written: Vec::new(),
        total_attachments: 0,
    };
    let result = build_document(msg, source, &mut ctx);
    if result.is_err() {
        for p in &ctx.written {
            let _ = fs::remove_file(p);
        }
    }
    result
}

fn build_document(
    msg: &dyn MessageObject,
    source: Source,
    ctx: &mut Ctx,
) -> ItemResult<Extraction> {
    let content = build_content(msg, 0, "", ctx)?;
    let mut doc = MessageDocument {
        format: DOCUMENT_FORMAT,
        version: DOCUMENT_VERSION,
        source,
        content,
    };
    let bytes = fit_document(&mut doc, ctx.limits.max_document_bytes)?;
    let mut warnings = Vec::new();
    collect_warnings(&doc.content, "", &mut warnings);
    let path = ctx.out_dir.join("message.json");
    write_new_file(&path, &bytes)?;
    ctx.written.push(path);
    Ok(Extraction {
        document_bytes: bytes,
        files: std::mem::take(&mut ctx.files),
        warnings,
    })
}

/// Serialize; when over the document limit, drop the largest optional parts
/// (RTF, HTML, text bodies, transport headers) in that order, flagging each.
fn fit_document(doc: &mut MessageDocument, max: usize) -> ItemResult<Vec<u8>> {
    let ser = |doc: &MessageDocument| {
        serde_json::to_vec(doc)
            .map_err(|_| ItemError::new(Code::Corrupt, "document serialization failed"))
    };
    let mut bytes = ser(doc)?;
    let mut step = 0;
    while bytes.len() > max {
        let c = &mut doc.content;
        let what = match step {
            0 if c.body.rtf.is_some() => {
                c.body.rtf = None;
                c.body.truncated.rtf = true;
                "rtf body"
            }
            1 if c.body.html.is_some() => {
                c.body.html = None;
                c.body.truncated.html = true;
                "html body"
            }
            2 if c.body.text.is_some() => {
                c.body.text = None;
                c.body.truncated.text = true;
                "text body"
            }
            3 if c.transport_headers.is_some() => {
                c.transport_headers = None;
                "transport headers"
            }
            0..=3 => {
                step += 1;
                continue;
            }
            _ => {
                return Err(ItemError::new(
                    Code::TooLarge,
                    "normalized document exceeds --max-document-bytes",
                ))
            }
        };
        step += 1;
        c.warnings.push(Warning {
            code: "document_truncated",
            message: format!("{what} dropped to fit --max-document-bytes"),
        });
        bytes = ser(doc)?;
    }
    Ok(bytes)
}

fn collect_warnings(c: &MessageContent, prefix: &str, out: &mut Vec<Warning>) {
    for w in &c.warnings {
        out.push(if prefix.is_empty() {
            w.clone()
        } else {
            Warning {
                code: w.code,
                message: format!("embedded message {prefix}: {}", w.message),
            }
        });
    }
    for a in &c.attachments {
        if let Some(e) = &a.embedded_message {
            collect_warnings(e, &a.key, out);
        }
    }
}

fn warn(ws: &mut Vec<Warning>, code: &'static str, message: impl Into<String>) {
    ws.push(Warning {
        code,
        message: message.into(),
    });
}

/// Code page for 8-bit strings: PR_MESSAGE_CODEPAGE, else PR_INTERNET_CPID.
fn message_codepage(props: &PropMap) -> Option<u32> {
    [id::MESSAGE_CODEPAGE, id::INTERNET_CPID]
        .into_iter()
        .filter_map(|p| match props.get(&p) {
            Some(PropValue::Int(n)) if *n > 0 => u32::try_from(*n).ok(),
            _ => None,
        })
        .find(|cp| known_codepage(*cp))
}

fn decode_raw_string(raw: &RawProp, cp: Option<u32>) -> Option<String> {
    match raw.ptype {
        PT_UNICODE => Some(utf16le_to_string(&raw.bytes)),
        PT_STRING8 => Some(decode_codepage(&raw.bytes, cp)),
        _ => None,
    }
}

/// A string property, read and capped at `max` UTF-8 bytes.
/// Returns (value, truncated).
fn string_value(
    msg: &dyn MessageObject,
    prop: u16,
    max: usize,
    cp: Option<u32>,
) -> ItemResult<Option<(String, bool)>> {
    match msg.props().get(&prop) {
        None => return Ok(None),
        Some(PropValue::Unicode(s)) => {
            let mut s = s.clone();
            let cut = truncate_utf8(&mut s, max);
            return Ok(Some((s, cut)));
        }
        Some(PropValue::String8(b)) => {
            let mut s = decode_codepage(b, cp);
            let cut = truncate_utf8(&mut s, max);
            return Ok(Some((s, cut)));
        }
        Some(PropValue::Deferred { .. }) => {}
        Some(_) => return Ok(None),
    }
    // Stored in a sub-node: read at most what can survive the cap.
    let Some(raw) = msg.read_prop(prop, max.saturating_mul(2).saturating_add(2))? else {
        return Ok(None);
    };
    let Some(mut s) = decode_raw_string(&raw, cp) else {
        return Ok(None);
    };
    let partial = raw.total > raw.bytes.len() as u64;
    let cut = truncate_utf8(&mut s, max);
    Ok(Some((s, cut || partial)))
}

fn short_string(
    msg: &dyn MessageObject,
    prop: u16,
    cp: Option<u32>,
    ws: &mut Vec<Warning>,
) -> Option<String> {
    match string_value(msg, prop, MAX_SHORT_STRING, cp) {
        Ok(Some((s, cut))) => {
            if cut {
                warn(
                    ws,
                    "property_truncated",
                    format!("property 0x{prop:04x} truncated"),
                );
            }
            Some(s)
        }
        Ok(None) => None,
        Err(e) => {
            warn(
                ws,
                "property_unreadable",
                format!("property 0x{prop:04x}: {}", e.code.as_str()),
            );
            None
        }
    }
}

fn int_prop(props: &PropMap, prop: u16) -> Option<i64> {
    match props.get(&prop)? {
        PropValue::Int(n) => Some(*n),
        _ => None,
    }
}

fn bool_prop(props: &PropMap, prop: u16) -> Option<bool> {
    match props.get(&prop)? {
        PropValue::Bool(b) => Some(*b),
        PropValue::Int(n) => Some(*n != 0),
        _ => None,
    }
}

fn time_prop(props: &PropMap, prop: u16) -> Option<String> {
    match props.get(&prop)? {
        PropValue::Time(t) => filetime_to_iso(*t),
        _ => None,
    }
}

/// PR_SUBJECT may start with SOH + prefix-length char (MS-PST, PidTagSubject prefix rules).
fn normalize_subject(mut s: String) -> String {
    if s.starts_with('\u{1}') {
        let mut chars = s.chars();
        chars.next();
        chars.next();
        s = chars.as_str().to_string();
    }
    s
}

fn non_empty(s: Option<String>) -> Option<String> {
    s.filter(|s| !s.is_empty())
}

/// An address from (name, addrtype, email, smtp) property ids. SMTP wins;
/// otherwise the native address with its address type.
fn address_from(
    lookup: &mut dyn FnMut(u16) -> Option<String>,
    name: u16,
    addrtype: u16,
    email: u16,
    smtp: u16,
) -> Option<Address> {
    let name = non_empty(lookup(name));
    let smtp = non_empty(lookup(smtp));
    let (address, address_type) = match smtp {
        Some(a) => (Some(a), Some("SMTP".to_string())),
        None => {
            let email = non_empty(lookup(email));
            let at = non_empty(lookup(addrtype));
            match email {
                Some(e) => (Some(e), at),
                None => (None, None),
            }
        }
    };
    if name.is_none() && address.is_none() {
        return None;
    }
    Some(Address {
        name,
        address,
        address_type,
    })
}

fn map_string(props: &PropMap, prop: u16, cp: Option<u32>) -> Option<String> {
    match props.get(&prop)? {
        PropValue::Unicode(s) => Some(s.clone()),
        PropValue::String8(b) => Some(decode_codepage(b, cp)),
        _ => None,
    }
}

fn build_content(
    msg: &dyn MessageObject,
    depth: u32,
    key_prefix: &str,
    ctx: &mut Ctx,
) -> ItemResult<MessageContent> {
    let props = msg.props();
    let cp = message_codepage(props);
    let mut ws = Vec::new();
    let mut c = MessageContent {
        message_class: short_string(msg, id::MESSAGE_CLASS, cp, &mut ws),
        subject: short_string(msg, id::SUBJECT, cp, &mut ws).map(normalize_subject),
        conversation_topic: short_string(msg, id::CONVERSATION_TOPIC, cp, &mut ws),
        ..Default::default()
    };

    // Originator: "from" is the represented sender, "sender" the actual one
    // when it differs (sent on behalf of).
    let mut lookup = |p: u16| short_string(msg, p, cp, &mut ws);
    let representing = address_from(
        &mut lookup,
        id::SENT_REPRESENTING_NAME,
        id::SENT_REPRESENTING_ADDRTYPE,
        id::SENT_REPRESENTING_EMAIL,
        id::SENT_REPRESENTING_SMTP_ADDRESS,
    );
    let sender = address_from(
        &mut lookup,
        id::SENDER_NAME,
        id::SENDER_ADDRTYPE,
        id::SENDER_EMAIL,
        id::SENDER_SMTP_ADDRESS,
    );
    match (representing, sender) {
        // The represented originator without an address, and an actual sender
        // with the same (or no) display name, are one person: use the sender's
        // complete address rather than reporting two half-addresses.
        (Some(r), Some(s)) if r.address.is_none() && (r.name.is_none() || r.name == s.name) => {
            c.from = Some(s);
        }
        (Some(r), s) => {
            c.sender = s.filter(|s| *s != r);
            c.from = Some(r);
        }
        (None, s) => c.from = s,
    }

    match msg.recipients(MAX_RECIPIENTS) {
        Ok((rows, total)) => {
            if total > rows.len() {
                warn(
                    &mut ws,
                    "recipients_truncated",
                    format!("{} of {total} recipients described", rows.len()),
                );
            }
            for row in rows {
                let kind = int_prop(&row, id::RECIPIENT_TYPE).unwrap_or(0);
                let mut lookup = |p: u16| map_string(&row, p, cp);
                let Some(addr) = address_from(
                    &mut lookup,
                    id::DISPLAY_NAME,
                    id::ADDRTYPE,
                    id::EMAIL_ADDRESS,
                    id::SMTP_ADDRESS,
                ) else {
                    continue;
                };
                // MAPI_P1 (0x10000000) marks resend copies; skip them.
                match kind {
                    1 => c.to.push(addr),
                    2 => c.cc.push(addr),
                    3 => c.bcc.push(addr),
                    _ => {}
                }
            }
        }
        Err(e) => warn(
            &mut ws,
            "recipients_unreadable",
            format!("recipient table: {}", e.code.as_str()),
        ),
    }

    c.sent_at = time_prop(props, id::CLIENT_SUBMIT_TIME);
    c.received_at = time_prop(props, id::MESSAGE_DELIVERY_TIME);
    c.created_at = time_prop(props, id::CREATION_TIME);
    c.modified_at = time_prop(props, id::LAST_MODIFICATION_TIME);
    c.internet_message_id = short_string(msg, id::INTERNET_MESSAGE_ID, cp, &mut ws);
    c.in_reply_to = short_string(msg, id::IN_REPLY_TO, cp, &mut ws);
    c.references = short_string(msg, id::REFERENCES, cp, &mut ws);
    c.importance = int_prop(props, id::IMPORTANCE);
    c.sensitivity = int_prop(props, id::SENSITIVITY);
    c.flags = int_prop(props, id::MESSAGE_FLAGS);

    match string_value(msg, id::TRANSPORT_HEADERS, MAX_TRANSPORT_HEADERS, cp) {
        Ok(Some((s, cut))) => {
            if cut {
                warn(
                    &mut ws,
                    "headers_truncated",
                    "transport headers truncated at 256 KiB",
                );
            }
            c.transport_headers = Some(s);
        }
        Ok(None) => {}
        Err(e) => warn(
            &mut ws,
            "headers_unreadable",
            format!("transport headers: {}", e.code.as_str()),
        ),
    }

    c.body = build_body(msg, cp, ctx.limits.max_body_bytes, &mut ws);

    let (attachments, any) = build_attachments(msg, depth, key_prefix, cp, ctx, &mut ws)?;
    c.has_attachments = any
        || (attachments.is_empty()
            && int_prop(props, id::MESSAGE_FLAGS).is_some_and(|f| f & MSGFLAG_HASATTACH != 0)
            && ws.iter().any(|w| w.code == "attachments_unreadable"));
    c.attachments = attachments;
    c.warnings = ws;
    Ok(c)
}

fn build_body(msg: &dyn MessageObject, cp: Option<u32>, max: usize, ws: &mut Vec<Warning>) -> Body {
    let mut body = Body::default();
    let mut truncated = Truncated::default();

    match string_value(msg, id::BODY, max, cp) {
        Ok(Some((s, cut))) => {
            body.text = Some(s);
            truncated.text = cut;
            if cut {
                warn(ws, "body_truncated", "text body truncated at limit");
            }
        }
        Ok(None) => {}
        Err(e) => warn(
            ws,
            "body_unreadable",
            format!("text body: {}", e.code.as_str()),
        ),
    }

    match read_html(msg, max, ws) {
        Ok(Some((s, cut))) => {
            body.html = Some(s);
            truncated.html = cut;
            if cut {
                warn(ws, "body_truncated", "html body truncated at limit");
            }
        }
        Ok(None) => {}
        Err(e) => warn(
            ws,
            "body_unreadable",
            format!("html body: {}", e.code.as_str()),
        ),
    }

    if body.text.is_none() && body.html.is_none() {
        match read_rtf(msg, max) {
            Ok(Some((s, cut))) => {
                body.rtf = Some(s);
                truncated.rtf = cut;
                if cut {
                    warn(ws, "body_truncated", "rtf body truncated at limit");
                }
            }
            Ok(None) => {}
            Err(e) => {
                if e.code == Code::TooLarge {
                    truncated.rtf = true;
                }
                warn(ws, "body_unreadable", format!("rtf body: {}", e.message));
            }
        }
    }
    body.truncated = truncated;
    body
}

fn read_html(
    msg: &dyn MessageObject,
    max: usize,
    ws: &mut Vec<Warning>,
) -> ItemResult<Option<(String, bool)>> {
    let props = msg.props();
    let ptype = match props.get(&id::HTML) {
        None => return Ok(None),
        Some(PropValue::Binary(_)) => PT_BINARY,
        Some(PropValue::Deferred { ptype, .. }) => *ptype,
        Some(PropValue::Unicode(_)) => PT_UNICODE,
        Some(PropValue::String8(_)) => PT_STRING8,
        Some(_) => return Ok(None),
    };
    if ptype != PT_BINARY {
        return string_value(msg, id::HTML, max, message_codepage(props));
    }
    let Some(raw) = msg.read_prop(id::HTML, max)? else {
        return Ok(None);
    };
    let partial = raw.total > raw.bytes.len() as u64;
    let cpid = match props.get(&id::INTERNET_CPID) {
        Some(PropValue::Int(n)) => u32::try_from(*n).ok(),
        _ => None,
    };
    let mut s = match cpid {
        Some(65001) => String::from_utf8_lossy(&raw.bytes).into_owned(),
        Some(cp) if known_codepage(cp) => decode_codepage(&raw.bytes, Some(cp)),
        _ => {
            // No usable PR_INTERNET_CPID: accept UTF-8 when the bytes are
            // valid UTF-8 (ignoring a cut at the cap), else Windows-1252.
            let valid = match std::str::from_utf8(&raw.bytes) {
                Ok(_) => true,
                Err(e) => partial && e.error_len().is_none(),
            };
            if valid {
                warn(
                    ws,
                    "html_charset_assumed",
                    "no PR_INTERNET_CPID; html decoded as UTF-8",
                );
                String::from_utf8_lossy(&raw.bytes).into_owned()
            } else {
                warn(
                    ws,
                    "html_charset_assumed",
                    "no PR_INTERNET_CPID; html decoded as Windows-1252",
                );
                decode_codepage(&raw.bytes, Some(1252))
            }
        }
    };
    crate::props::trim_nuls(&mut s);
    let cut = truncate_utf8(&mut s, max);
    Ok(Some((s, cut || partial)))
}

/// Decompressed RTF (MS-OXRTFCP), bounded.
fn read_rtf(msg: &dyn MessageObject, max: usize) -> ItemResult<Option<(String, bool)>> {
    if !msg.props().contains_key(&id::RTF_COMPRESSED) {
        return Ok(None);
    }
    let cap = max.saturating_add(16);
    let Some(raw) = msg.read_prop(id::RTF_COMPRESSED, cap)? else {
        return Ok(None);
    };
    if raw.total > raw.bytes.len() as u64 {
        return Err(ItemError::new(
            Code::TooLarge,
            "compressed rtf exceeds --max-body-bytes",
        ));
    }
    decompress_rtf_bounded(&raw.bytes, max).map(Some)
}

/// Validate the MS-OXRTFCP header before handing data to `compressed-rtf`,
/// which trusts it (pre-allocates RAWSIZE and slices by it).
pub fn decompress_rtf_bounded(data: &[u8], max: usize) -> ItemResult<(String, bool)> {
    if data.len() < 16 {
        return Err(ItemError::corrupt("rtf header truncated"));
    }
    let rd = |o: usize| u32::from_le_bytes([data[o], data[o + 1], data[o + 2], data[o + 3]]);
    let comp_size = rd(0) as usize;
    let raw_size = rd(4) as usize;
    let comp_type = rd(8);
    if comp_size.checked_add(4) != Some(data.len()) {
        return Err(ItemError::corrupt("rtf size mismatch"));
    }
    // LZFu expands at most 17 bytes per 2-byte reference: < 9x the input.
    let bound = data.len().saturating_mul(9);
    match comp_type {
        0x7546_5A4C => {
            if raw_size > bound || raw_size > max.saturating_mul(4) {
                return Err(ItemError::new(
                    Code::TooLarge,
                    "rtf raw size implausible or over limit",
                ));
            }
        }
        0x414C_454D => {
            if raw_size.checked_add(16).is_none_or(|end| end > data.len()) {
                return Err(ItemError::corrupt("uncompressed rtf size mismatch"));
            }
        }
        _ => return Err(ItemError::corrupt("unknown rtf compression type")),
    }
    let (res, _) = guard("rtf", || {
        compressed_rtf::decompress_rtf(data)
            .map_err(|_| ItemError::corrupt("rtf decompression failed"))
    });
    let mut s = res?;
    let cut = truncate_utf8(&mut s, max);
    Ok((s, cut))
}

fn method_name(m: Option<i64>) -> &'static str {
    match m {
        Some(1) => "by_value",
        Some(2) | Some(3) => "by_reference",
        Some(4) => "by_reference_only",
        Some(5) => "embedded_message",
        Some(6) => "ole",
        _ => "unsupported",
    }
}

fn corrupt_attachment(key: String) -> AttachmentDoc {
    AttachmentDoc {
        key,
        method: "unsupported",
        file_name: None,
        display_name: None,
        mime_type: None,
        content_id: None,
        inline: false,
        hidden: false,
        size_bytes: None,
        extracted: false,
        skip_reason: Some("corrupt"),
        embedded_message: None,
    }
}

/// Returns the attachment descriptors and whether the message has any
/// attachment rows at all.
fn build_attachments(
    msg: &dyn MessageObject,
    depth: u32,
    key_prefix: &str,
    cp: Option<u32>,
    ctx: &mut Ctx,
    ws: &mut Vec<Warning>,
) -> ItemResult<(Vec<AttachmentDoc>, bool)> {
    let rows = match msg.attachments() {
        Ok(rows) => rows,
        Err(e) => {
            warn(
                ws,
                "attachments_unreadable",
                format!("attachment table: {}", e.code.as_str()),
            );
            return Ok((Vec::new(), false));
        }
    };
    let any = !rows.is_empty();
    let limit = ctx.limits.max_attachments_per_message;
    let describe_limit = limit.saturating_mul(DESCRIBE_FACTOR).max(limit);
    let mut out = Vec::new();
    for (i, row) in rows.iter().enumerate() {
        let key = format!("{key_prefix}{i}");
        if i >= describe_limit || ctx.total_attachments >= MAX_TOTAL_ATTACHMENTS {
            warn(
                ws,
                "attachments_truncated",
                format!("{} attachments not described (limit)", rows.len() - i),
            );
            break;
        }
        ctx.total_attachments += 1;
        let att = match msg.attachment(*row) {
            Ok(a) => a,
            Err(e) => {
                warn(
                    ws,
                    "attachment_unreadable",
                    format!("attachment {key}: {}", e.code.as_str()),
                );
                out.push(corrupt_attachment(key));
                continue;
            }
        };
        let over_limit = i >= limit;
        out.push(describe_attachment(
            att.as_ref(),
            key,
            over_limit,
            depth,
            cp,
            ctx,
            ws,
        )?);
    }
    if rows.len() > limit {
        warn(
            ws,
            "attachments_limit_exceeded",
            format!(
                "{} attachments beyond --max-attachments-per-message not extracted",
                rows.len() - limit
            ),
        );
    }
    Ok((out, any))
}

fn describe_attachment(
    att: &dyn AttachmentObject,
    key: String,
    over_limit: bool,
    depth: u32,
    cp: Option<u32>,
    ctx: &mut Ctx,
    ws: &mut Vec<Warning>,
) -> ItemResult<AttachmentDoc> {
    let props = att.props();
    let s = |p: u16| {
        non_empty(map_string(props, p, cp).map(|mut s| {
            truncate_utf8(&mut s, MAX_SHORT_STRING);
            s
        }))
    };
    let method = method_name(int_prop(props, id::ATTACH_METHOD));
    let content_id = s(id::ATTACH_CONTENT_ID);
    let flags = int_prop(props, id::ATTACH_FLAGS).unwrap_or(0);
    let mut doc = AttachmentDoc {
        key,
        method,
        file_name: s(id::ATTACH_LONG_FILENAME).or_else(|| s(id::ATTACH_FILENAME)),
        display_name: s(id::DISPLAY_NAME),
        mime_type: s(id::ATTACH_MIME_TAG),
        inline: content_id.is_some() || flags & ATT_MHTML_REF != 0,
        content_id,
        hidden: bool_prop(props, id::ATTACHMENT_HIDDEN).unwrap_or(false),
        size_bytes: int_prop(props, id::ATTACH_SIZE).and_then(|n| u64::try_from(n).ok()),
        extracted: false,
        skip_reason: None,
        embedded_message: None,
    };

    match method {
        "by_value" => {
            let size = match att.data_size() {
                Ok(Some(size)) => size,
                Ok(None) => {
                    doc.skip_reason = Some("corrupt");
                    warn(
                        ws,
                        "attachment_unreadable",
                        format!("attachment {}: no binary data", doc.key),
                    );
                    return Ok(doc);
                }
                Err(e) => {
                    doc.skip_reason = Some("corrupt");
                    warn(
                        ws,
                        "attachment_unreadable",
                        format!("attachment {}: {}", doc.key, e.code.as_str()),
                    );
                    return Ok(doc);
                }
            };
            doc.size_bytes = Some(size);
            if over_limit {
                doc.skip_reason = Some("limit_exceeded");
            } else if size > ctx.limits.max_attachment_bytes {
                doc.skip_reason = Some("too_large");
            } else {
                match write_attachment(att, &doc.key, ctx) {
                    Ok(file) => {
                        doc.size_bytes = Some(file.size_bytes);
                        doc.extracted = true;
                        ctx.files.push(file);
                    }
                    Err(e) if e.code == Code::IoError => return Err(e),
                    Err(e) => {
                        doc.skip_reason = Some(if e.code == Code::TooLarge {
                            "too_large"
                        } else {
                            "corrupt"
                        });
                        warn(
                            ws,
                            "attachment_unreadable",
                            format!("attachment {}: {}", doc.key, e.code.as_str()),
                        );
                    }
                }
            }
        }
        "embedded_message" => {
            if over_limit {
                doc.skip_reason = Some("limit_exceeded");
            } else if depth + 1 > ctx.limits.max_embedded_depth.min(HARD_MAX_EMBEDDED_DEPTH) {
                doc.skip_reason = Some("depth_exceeded");
            } else {
                match att.embedded() {
                    Ok(m) => {
                        let prefix = format!("{}.", doc.key);
                        let content = build_content(m.as_ref(), depth + 1, &prefix, ctx)?;
                        doc.embedded_message = Some(Box::new(content));
                    }
                    Err(e) => {
                        doc.skip_reason = Some("corrupt");
                        warn(
                            ws,
                            "attachment_unreadable",
                            format!("embedded message {}: {}", doc.key, e.code.as_str()),
                        );
                    }
                }
            }
        }
        _ => doc.skip_reason = Some("unsupported_method"),
    }
    Ok(doc)
}

struct HashingWriter<W: Write> {
    inner: W,
    hasher: Sha256,
    count: u64,
}

impl<W: Write> Write for HashingWriter<W> {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        let n = self.inner.write(buf)?;
        self.hasher.update(&buf[..n]);
        self.count += n as u64;
        Ok(n)
    }

    fn flush(&mut self) -> io::Result<()> {
        self.inner.flush()
    }
}

fn create_new(path: &Path) -> ItemResult<File> {
    OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .map_err(|e| ItemError::io(&e, "cannot create output file"))
}

fn write_new_file(path: &Path, bytes: &[u8]) -> ItemResult<()> {
    let mut f = create_new(path)?;
    // No fsync: the caller consumes and deletes these files right away; the
    // response line is written only after every write completed.
    f.write_all(bytes)
        .map_err(|e| ItemError::io(&e, "cannot write output file"))
}

fn write_attachment(
    att: &dyn AttachmentObject,
    key: &str,
    ctx: &mut Ctx,
) -> ItemResult<ExtractedFile> {
    let name = format!("att-{key}.bin");
    let path = ctx.out_dir.join(&name);
    let file = create_new(&path)?;
    ctx.written.push(path.clone());
    let mut w = HashingWriter {
        inner: BufWriter::with_capacity(64 * 1024, file),
        hasher: Sha256::new(),
        count: 0,
    };
    let result = att
        .write_data(&mut w, ctx.limits.max_attachment_bytes)
        .and_then(|_| {
            w.flush()
                .map_err(|e| ItemError::io(&e, "cannot write attachment file"))
        });
    if let Err(e) = result {
        let _ = fs::remove_file(&path);
        ctx.written.pop();
        return Err(e);
    }
    let digest = w.hasher.finalize();
    Ok(ExtractedFile {
        key: key.to_string(),
        file: format!("{}{name}", ctx.report_prefix),
        size_bytes: w.count,
        sha256: digest.iter().map(|b| format!("{b:02x}")).collect(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::AttachmentRow;
    use crate::props::{PT_BINARY, PT_UNICODE};
    use std::collections::BTreeMap;

    fn utf16(s: &str) -> Vec<u8> {
        s.encode_utf16().flat_map(|u| u.to_le_bytes()).collect()
    }

    #[derive(Default, Clone)]
    struct FakeAtt {
        props: PropMap,
        data: Option<Vec<u8>>,
        embedded: Option<FakeMsg>,
    }

    #[derive(Default, Clone)]
    struct FakeMsg {
        props: PropMap,
        raw: BTreeMap<u16, (u16, Vec<u8>)>,
        recipients: Vec<PropMap>,
        attachments: Vec<FakeAtt>,
    }

    impl MessageObject for FakeMsg {
        fn props(&self) -> &PropMap {
            &self.props
        }
        fn read_prop(&self, id: u16, cap: usize) -> ItemResult<Option<RawProp>> {
            Ok(self.raw.get(&id).map(|(ptype, b)| RawProp {
                ptype: *ptype,
                bytes: b[..b.len().min(cap)].to_vec(),
                total: b.len() as u64,
            }))
        }
        fn recipients(&self, max: usize) -> ItemResult<(Vec<PropMap>, usize)> {
            Ok((
                self.recipients.iter().take(max).cloned().collect(),
                self.recipients.len(),
            ))
        }
        fn attachments(&self) -> ItemResult<Vec<AttachmentRow>> {
            Ok((0..self.attachments.len() as u32)
                .map(|nid| AttachmentRow { nid })
                .collect())
        }
        fn attachment(&self, row: AttachmentRow) -> ItemResult<Box<dyn AttachmentObject>> {
            Ok(Box::new(self.attachments[row.nid as usize].clone()))
        }
    }

    impl AttachmentObject for FakeAtt {
        fn props(&self) -> &PropMap {
            &self.props
        }
        fn data_size(&self) -> ItemResult<Option<u64>> {
            Ok(self.data.as_ref().map(|d| d.len() as u64))
        }
        fn write_data(&self, w: &mut dyn Write, max: u64) -> ItemResult<u64> {
            let d = self.data.as_ref().unwrap();
            if d.len() as u64 > max {
                return Err(ItemError::new(Code::TooLarge, "x"));
            }
            w.write_all(d).unwrap();
            Ok(d.len() as u64)
        }
        fn embedded(&self) -> ItemResult<Box<dyn MessageObject>> {
            self.embedded
                .clone()
                .map(|m| Box::new(m) as Box<dyn MessageObject>)
                .ok_or_else(|| ItemError::corrupt("none"))
        }
    }

    fn text_msg(subject: &str) -> FakeMsg {
        let mut m = FakeMsg::default();
        m.props
            .insert(id::SUBJECT, PropValue::Unicode(subject.into()));
        m.props
            .insert(id::MESSAGE_CLASS, PropValue::Unicode("IPM.Note".into()));
        m
    }

    fn by_value(name: &str, data: &[u8]) -> FakeAtt {
        let mut a = FakeAtt::default();
        a.props.insert(id::ATTACH_METHOD, PropValue::Int(1));
        a.props
            .insert(id::ATTACH_LONG_FILENAME, PropValue::Unicode(name.into()));
        a.data = Some(data.to_vec());
        a
    }

    fn embedded(inner: FakeMsg) -> FakeAtt {
        let mut a = FakeAtt::default();
        a.props.insert(id::ATTACH_METHOD, PropValue::Int(5));
        a.props
            .insert(id::DISPLAY_NAME, PropValue::Unicode("Fwd: hello".into()));
        a.embedded = Some(inner);
        a
    }

    fn run(msg: &FakeMsg, limits: &Limits) -> (serde_json::Value, Extraction, tempfile::TempDir) {
        let dir = tempfile::tempdir().unwrap();
        let src = Source {
            message_id: "m:00200024".into(),
            folder_id: Some("f:00008062".into()),
        };
        let ex = extract_message(msg, src, limits, dir.path(), "r1/").unwrap();
        let v: serde_json::Value = serde_json::from_slice(&ex.document_bytes).unwrap();
        (v, ex, dir)
    }

    #[test]
    fn embedded_messages_recurse_with_dotted_keys() {
        let mut inner2 = text_msg("level 2");
        inner2.attachments.push(by_value("deep.txt", b"deep"));
        let mut inner = text_msg("level 1");
        inner.attachments.push(by_value("inner.txt", b"inner"));
        inner.attachments.push(embedded(inner2));
        let mut top = text_msg("\u{1}\u{5}Fwd: top");
        top.attachments.push(by_value("a.txt", b"a"));
        top.attachments.push(embedded(inner));

        let (v, ex, dir) = run(&top, &Limits::default());
        assert_eq!(v["format"], "nubarca-mail-message");
        assert_eq!(v["subject"], "Fwd: top");
        let a1 = &v["attachments"][1];
        assert_eq!(a1["method"], "embedded_message");
        assert_eq!(a1["extracted"], false);
        assert_eq!(a1["skipReason"], serde_json::Value::Null);
        let e = &a1["embeddedMessage"];
        assert_eq!(e["subject"], "level 1");
        assert!(e.get("format").is_none() && e.get("source").is_none());
        assert_eq!(e["attachments"][0]["key"], "1.0");
        assert_eq!(
            e["attachments"][1]["embeddedMessage"]["attachments"][0]["key"],
            "1.1.0"
        );
        let keys: Vec<_> = ex.files.iter().map(|f| f.key.as_str()).collect();
        assert_eq!(keys, ["0", "1.0", "1.1.0"]);
        assert_eq!(ex.files[1].file, "r1/att-1.0.bin");
        assert_eq!(
            std::fs::read(dir.path().join("att-1.1.0.bin")).unwrap(),
            b"deep"
        );
    }

    #[test]
    fn embedded_depth_is_bounded() {
        let mut m = text_msg("leaf");
        for i in 0..6 {
            let mut outer = text_msg(&format!("level {i}"));
            outer.attachments.push(embedded(m));
            m = outer;
        }
        let limits = Limits {
            max_embedded_depth: 2,
            ..Limits::default()
        };
        let (v, _, _) = run(&m, &limits);
        let l1 = &v["attachments"][0]["embeddedMessage"];
        let l2 = &l1["attachments"][0]["embeddedMessage"];
        assert!(l2.is_object());
        let a3 = &l2["attachments"][0];
        assert_eq!(a3["skipReason"], "depth_exceeded");
        assert!(a3["embeddedMessage"].is_null());
    }

    #[test]
    fn rtf_body_only_when_no_text_or_html() {
        let rtf = "{\\rtf1\\ansi Ciao}";
        let compressed = compressed_rtf::compress_rtf(rtf).unwrap();
        let mut m = text_msg("rtf");
        m.props
            .insert(id::RTF_COMPRESSED, PropValue::Binary(compressed.clone()));
        m.raw
            .insert(id::RTF_COMPRESSED, (PT_BINARY, compressed.clone()));
        let (v, _, _) = run(&m, &Limits::default());
        assert_eq!(v["body"]["rtf"], rtf);
        assert!(v["body"]["text"].is_null() && v["body"]["html"].is_null());

        m.props.insert(id::BODY, PropValue::Unicode("plain".into()));
        let (v, _, _) = run(&m, &Limits::default());
        assert_eq!(v["body"]["text"], "plain");
        assert!(v["body"]["rtf"].is_null());
    }

    #[test]
    fn rtf_header_is_validated() {
        assert!(decompress_rtf_bounded(&[0u8; 8], 100).is_err());
        let mut bad = vec![0u8; 32];
        bad[0..4].copy_from_slice(&28u32.to_le_bytes());
        bad[4..8].copy_from_slice(&u32::MAX.to_le_bytes());
        bad[8..12].copy_from_slice(&0x7546_5A4Cu32.to_le_bytes());
        assert_eq!(
            decompress_rtf_bounded(&bad, 100).unwrap_err().code,
            Code::TooLarge
        );
        bad[8..12].copy_from_slice(&0x414C_454Du32.to_le_bytes());
        assert_eq!(
            decompress_rtf_bounded(&bad, 100).unwrap_err().code,
            Code::Corrupt
        );
    }

    #[test]
    fn bodies_are_bounded_and_flagged() {
        let mut m = text_msg("big");
        let text = "è".repeat(100);
        m.props.insert(
            id::BODY,
            PropValue::Deferred {
                ptype: PT_UNICODE,
                nid: 1,
            },
        );
        m.raw.insert(id::BODY, (PT_UNICODE, utf16(&text)));
        let html = "<p>".to_string() + &"x".repeat(200) + "</p>";
        m.props.insert(
            id::HTML,
            PropValue::Deferred {
                ptype: PT_BINARY,
                nid: 2,
            },
        );
        m.raw.insert(id::HTML, (PT_BINARY, html.into_bytes()));
        m.props.insert(id::INTERNET_CPID, PropValue::Int(65001));
        let limits = Limits {
            max_body_bytes: 51,
            ..Limits::default()
        };
        let (v, ex, _) = run(&m, &limits);
        assert_eq!(v["body"]["text"].as_str().unwrap().len(), 50);
        assert_eq!(v["body"]["truncated"]["text"], true);
        assert_eq!(v["body"]["html"].as_str().unwrap().len(), 51);
        assert_eq!(v["body"]["truncated"]["html"], true);
        assert!(ex.warnings.iter().any(|w| w.code == "body_truncated"));
    }

    #[test]
    fn html_codepage_decoding() {
        let mut m = text_msg("cp");
        m.props
            .insert(id::HTML, PropValue::Binary(b"<p>caf\xe9</p>".to_vec()));
        m.raw
            .insert(id::HTML, (PT_BINARY, b"<p>caf\xe9</p>".to_vec()));
        m.props.insert(id::INTERNET_CPID, PropValue::Int(1252));
        let (v, _, _) = run(&m, &Limits::default());
        assert_eq!(v["body"]["html"], "<p>café</p>");
    }

    #[test]
    fn addresses_prefer_smtp_and_recipient_types() {
        let mut m = text_msg("addr");
        m.props
            .insert(id::SENT_REPRESENTING_NAME, PropValue::Unicode("Ada".into()));
        m.props.insert(
            id::SENT_REPRESENTING_EMAIL,
            PropValue::Unicode("/o=EX/cn=ada".into()),
        );
        m.props.insert(
            id::SENT_REPRESENTING_ADDRTYPE,
            PropValue::Unicode("EX".into()),
        );
        m.props.insert(
            id::SENT_REPRESENTING_SMTP_ADDRESS,
            PropValue::Unicode("ada@example.test".into()),
        );
        m.props
            .insert(id::SENDER_NAME, PropValue::Unicode("Ada".into()));
        m.props.insert(
            id::SENDER_SMTP_ADDRESS,
            PropValue::Unicode("ada@example.test".into()),
        );
        for (kind, name) in [(1, "To1"), (2, "Cc1"), (3, "Bcc1"), (1, "To2")] {
            let mut r = PropMap::new();
            r.insert(id::RECIPIENT_TYPE, PropValue::Int(kind));
            r.insert(id::DISPLAY_NAME, PropValue::Unicode(name.into()));
            r.insert(
                id::EMAIL_ADDRESS,
                PropValue::String8(format!("{name}@x.test").into_bytes()),
            );
            r.insert(id::ADDRTYPE, PropValue::Unicode("SMTP".into()));
            m.recipients.push(r);
        }
        let (v, _, _) = run(&m, &Limits::default());
        assert_eq!(v["from"]["address"], "ada@example.test");
        assert_eq!(v["from"]["addressType"], "SMTP");
        assert!(v["sender"].is_null());
        assert_eq!(v["to"].as_array().unwrap().len(), 2);
        assert_eq!(v["cc"][0]["name"], "Cc1");
        assert_eq!(v["bcc"][0]["address"], "Bcc1@x.test");
    }

    #[test]
    fn attachment_limits_and_unsupported_methods() {
        let mut m = text_msg("limits");
        m.attachments.push(by_value("small.bin", &[1; 10]));
        m.attachments.push(by_value("big.bin", &[2; 100]));
        let mut by_ref = FakeAtt::default();
        by_ref.props.insert(id::ATTACH_METHOD, PropValue::Int(3));
        m.attachments.push(by_ref);
        let mut ole = FakeAtt::default();
        ole.props.insert(id::ATTACH_METHOD, PropValue::Int(6));
        m.attachments.push(ole);
        m.attachments.push(by_value("over.bin", &[3; 1]));
        let limits = Limits {
            max_attachment_bytes: 50,
            max_attachments_per_message: 4,
            ..Limits::default()
        };
        let (v, ex, dir) = run(&m, &limits);
        let a = v["attachments"].as_array().unwrap();
        assert_eq!(a[0]["extracted"], true);
        assert_eq!(a[1]["skipReason"], "too_large");
        assert_eq!(a[1]["sizeBytes"], 100);
        assert_eq!(a[2]["method"], "by_reference");
        assert_eq!(a[2]["skipReason"], "unsupported_method");
        assert_eq!(a[3]["method"], "ole");
        assert_eq!(a[4]["skipReason"], "limit_exceeded");
        assert_eq!(ex.files.len(), 1);
        assert!(!dir.path().join("att-1.bin").exists());
    }

    #[test]
    fn document_limit_drops_bodies_then_fails() {
        let mut m = text_msg("doc");
        m.props
            .insert(id::BODY, PropValue::Unicode("y".repeat(5000)));
        let limits = Limits {
            max_document_bytes: 2000,
            ..Limits::default()
        };
        let (v, _, _) = run(&m, &limits);
        assert!(v["body"]["text"].is_null());
        assert_eq!(v["body"]["truncated"]["text"], true);

        let limits = Limits {
            max_document_bytes: 10,
            ..Limits::default()
        };
        let dir = tempfile::tempdir().unwrap();
        let src = Source {
            message_id: "m:00000004".into(),
            folder_id: None,
        };
        let err = extract_message(&m, src, &limits, dir.path(), "")
            .err()
            .unwrap();
        assert_eq!(err.code, Code::TooLarge);
        assert_eq!(std::fs::read_dir(dir.path()).unwrap().count(), 0);
    }
}
