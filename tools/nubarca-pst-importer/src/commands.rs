//! The four commands of protocol `nubarca-pst/1`.

use std::collections::HashSet;
use std::fs;
use std::io::{self, BufRead, BufWriter, Read, Write};
use std::path::{Path, PathBuf};

use serde::Serialize;

use crate::engine::{self, Engine};
use crate::error::{guard, Code, ItemError, ItemResult};
use crate::limits::Limits;
use crate::model::{
    folder_id, message_id, parse_message_id, EndEvent, ExtractedEvent, FatalEvent, FolderEvent,
    ListErrorEvent, MessageEvent, ProbeEvent, RequestErrorEvent, Source, PROTOCOL,
};
use crate::normalize::{extract_message, Extraction};
use crate::{Command, Invocation};

/// Longest accepted `serve` request line.
const MAX_REQUEST_LINE: usize = 64 * 1024;

const EXIT_OK: u8 = 0;
const EXIT_FATAL: u8 = 2;

struct Out {
    w: BufWriter<io::Stdout>,
}

impl Out {
    fn new() -> Self {
        Out {
            w: BufWriter::with_capacity(64 * 1024, io::stdout()),
        }
    }

    fn emit<T: Serialize>(&mut self, event: &T) -> io::Result<()> {
        serde_json::to_writer(&mut self.w, event).map_err(io::Error::other)?;
        self.w.write_all(b"\n")
    }

    fn flush(&mut self) -> io::Result<()> {
        self.w.flush()
    }

    fn fatal(&mut self, err: &ItemError) -> io::Result<u8> {
        self.emit(&FatalEvent {
            kind: "fatal",
            code: err.code.as_str(),
            message: &err.message,
        })?;
        self.flush()?;
        Ok(EXIT_FATAL)
    }
}

/// Holds the engine and reopens it after a recovered parser panic (the
/// library's internal state may be poisoned by an unwinding panic).
struct Session {
    path: PathBuf,
    engine: Box<dyn Engine>,
}

impl Session {
    fn open(path: &Path) -> ItemResult<Session> {
        let (res, _) = guard("open", || engine::open(path, true));
        Ok(Session {
            path: path.to_path_buf(),
            engine: res?,
        })
    }

    fn run<T>(
        &mut self,
        what: &str,
        f: impl FnOnce(&dyn Engine) -> ItemResult<T>,
    ) -> ItemResult<T> {
        let engine = &*self.engine;
        let (res, panicked) = guard(what, || f(engine));
        if panicked {
            // Structure was validated at first open; skip re-validation.
            let (reopened, _) = guard("reopen", || engine::open(&self.path, false));
            self.engine = reopened?;
        }
        res
    }
}

pub fn run(inv: Invocation) -> io::Result<u8> {
    let mut out = Out::new();
    match inv.command {
        Command::Probe => probe(&inv.pst, &mut out),
        Command::List => list(&inv.pst, &inv.limits, &mut out),
        Command::Serve { ref work_dir } => serve(&inv.pst, work_dir, &inv.limits, &mut out),
        Command::Extract {
            ref message,
            ref out_dir,
        } => extract(&inv.pst, message, out_dir, &inv.limits, &mut out),
    }
}

fn probe(pst: &Path, out: &mut Out) -> io::Result<u8> {
    let mut session = match Session::open(pst) {
        Ok(s) => s,
        Err(e) => return out.fatal(&e),
    };
    let header = session.engine.header();
    let name = match session.run("store", |e| e.store_display_name()) {
        Ok(n) => n,
        Err(e) => return out.fatal(&e),
    };
    out.emit(&ProbeEvent {
        kind: "probe",
        protocol: PROTOCOL,
        format: header.format.as_str(),
        encryption: header.encryption.as_str(),
        store_display_name: name.as_deref(),
        file_size: header.file_size,
    })?;
    out.flush()?;
    Ok(EXIT_OK)
}

struct Frame {
    nid: u32,
    parent: Option<u32>,
    path: Vec<String>,
    depth: u32,
}

fn list(pst: &Path, limits: &Limits, out: &mut Out) -> io::Result<u8> {
    let mut session = match Session::open(pst) {
        Ok(s) => s,
        Err(e) => return out.fatal(&e),
    };
    let root = match session.run("store", |e| e.ipm_subtree()) {
        Ok(r) => r,
        Err(e) => return out.fatal(&e),
    };
    let (mut folders, mut messages, mut errors) = (0u64, 0u64, 0u64);
    let mut visited = HashSet::new();
    let mut stack = vec![Frame {
        nid: root,
        parent: None,
        path: Vec::new(),
        depth: 0,
    }];
    let mut since_flush = 0usize;
    while let Some(frame) = stack.pop() {
        let fid = folder_id(frame.nid);
        let mut error = |out: &mut Out, code: Code, msg: &str| -> io::Result<()> {
            errors += 1;
            out.emit(&ListErrorEvent {
                kind: "error",
                scope: "folder",
                id: fid.clone(),
                code: code.as_str(),
                message: msg,
            })
        };
        if !visited.insert(frame.nid) {
            error(out, Code::Corrupt, "folder hierarchy loop")?;
            continue;
        }
        if frame.depth > limits.max_folder_depth {
            error(
                out,
                Code::LimitExceeded,
                "folder deeper than --max-folder-depth",
            )?;
            continue;
        }
        let info = match session.run("folder", |e| e.folder(frame.nid)) {
            Ok(info) => info,
            Err(e) if frame.parent.is_none() => {
                return out.fatal(&ItemError::new(
                    e.code,
                    format!("IPM subtree unreadable: {}", e.message),
                ))
            }
            Err(e) => {
                error(out, e.code, &e.message)?;
                continue;
            }
        };
        let mut path = frame.path;
        path.push(info.name.clone().unwrap_or_default());
        folders += 1;
        out.emit(&FolderEvent {
            kind: "folder",
            folder_id: fid.clone(),
            parent_folder_id: frame.parent.map(folder_id),
            name: info.name.as_deref(),
            path: &path,
            depth: frame.depth,
            container_class: info.container_class.as_deref(),
            content_count: info.content_count,
        })?;
        for m in &info.messages {
            if m & 0x1F != 0x04 {
                errors += 1;
                out.emit(&ListErrorEvent {
                    kind: "error",
                    scope: "message",
                    id: message_id(*m),
                    code: Code::Unsupported.as_str(),
                    message: "contents-table row is not a normal message",
                })?;
                continue;
            }
            messages += 1;
            out.emit(&MessageEvent {
                kind: "message",
                message_id: message_id(*m),
                folder_id: fid.clone(),
            })?;
            since_flush += 1;
            if since_flush >= 512 {
                out.flush()?;
                since_flush = 0;
            }
        }
        for child in info.subfolders.iter().rev() {
            stack.push(Frame {
                nid: *child,
                parent: Some(frame.nid),
                path: path.clone(),
                depth: frame.depth + 1,
            });
        }
        out.flush()?;
    }
    out.emit(&EndEvent {
        kind: "end",
        folders,
        messages,
        errors,
    })?;
    out.flush()?;
    Ok(EXIT_OK)
}

/// Extract one message into `dir` (which must exist and be empty of our
/// files). `prefix` is prepended to reported paths.
fn extract_into(
    session: &mut Session,
    nid: u32,
    dir: &Path,
    prefix: &str,
    limits: &Limits,
) -> ItemResult<Extraction> {
    session.run("message", |e| {
        let (msg, parent) = e.message(nid)?;
        let source = Source {
            message_id: message_id(nid),
            folder_id: parent.map(folder_id),
        };
        extract_message(msg.as_ref(), source, limits, dir, prefix)
    })
}

fn extract(
    pst: &Path,
    message: &str,
    out_dir: &Path,
    limits: &Limits,
    out: &mut Out,
) -> io::Result<u8> {
    let Some(nid) = parse_message_id(message) else {
        return out.fatal(&ItemError::new(Code::BadRequest, "invalid message id"));
    };
    let mut session = match Session::open(pst) {
        Ok(s) => s,
        Err(e) => return out.fatal(&e),
    };
    if let Err(e) = fs::create_dir_all(out_dir) {
        return out.fatal(&ItemError::io(&e, "cannot create output directory"));
    }
    match extract_into(&mut session, nid, out_dir, "", limits) {
        Ok(ex) => {
            out.emit(&ExtractedEvent {
                kind: "extracted",
                request_id: None,
                message_id: message_id(nid),
                document: "message.json".into(),
                document_bytes: ex.document_bytes.len() as u64,
                attachments: ex.files,
                warnings: ex.warnings,
            })?;
            out.flush()?;
            Ok(EXIT_OK)
        }
        Err(e) => out.fatal(&e),
    }
}

fn valid_request_id(s: &str) -> bool {
    (1..=64).contains(&s.len())
        && s.bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'_' || b == b'-')
}

/// Read one line of at most `max` bytes. Returns None at EOF; an over-long
/// line is consumed and reported as `Err(())`.
fn read_bounded_line(r: &mut impl BufRead, max: usize) -> io::Result<Option<Result<String, ()>>> {
    let mut buf = Vec::new();
    let n = r
        .by_ref()
        .take(max as u64 + 1)
        .read_until(b'\n', &mut buf)?;
    if n == 0 {
        return Ok(None);
    }
    if buf.last() != Some(&b'\n') && buf.len() > max {
        // Discard the rest of the line.
        let mut sink = Vec::new();
        loop {
            sink.clear();
            let m = r.by_ref().take(64 * 1024).read_until(b'\n', &mut sink)?;
            if m == 0 || sink.last() == Some(&b'\n') {
                break;
            }
        }
        return Ok(Some(Err(())));
    }
    Ok(Some(String::from_utf8(buf).map_err(|_| ())))
}

fn serve(pst: &Path, work_dir: &Path, limits: &Limits, out: &mut Out) -> io::Result<u8> {
    let work_dir = match fs::canonicalize(work_dir) {
        Ok(p) if p.is_dir() => p,
        Ok(_) => {
            return out.fatal(&ItemError::new(
                Code::IoError,
                "work dir is not a directory",
            ))
        }
        Err(e) => return out.fatal(&ItemError::io(&e, "work dir unavailable")),
    };
    let mut session = match Session::open(pst) {
        Ok(s) => s,
        Err(e) => return out.fatal(&e),
    };
    let stdin = io::stdin();
    let mut input = stdin.lock();
    loop {
        let line = match read_bounded_line(&mut input, MAX_REQUEST_LINE)? {
            None => break, // EOF: shutdown.
            Some(line) => line,
        };
        let line = match line {
            Ok(l) => l,
            Err(()) => {
                request_error(
                    out,
                    None,
                    None,
                    Code::BadRequest,
                    "request line too long or not UTF-8",
                )?;
                continue;
            }
        };
        let trimmed = line.trim();
        if trimmed.is_empty() {
            continue;
        }
        let req: serde_json::Value = match serde_json::from_str(trimmed) {
            Ok(v) => v,
            Err(_) => {
                request_error(out, None, None, Code::BadRequest, "request is not JSON")?;
                continue;
            }
        };
        match req.get("op").and_then(|v| v.as_str()) {
            Some("shutdown") => {
                out.emit(&serde_json::json!({ "type": "shutdown" }))?;
                out.flush()?;
                break;
            }
            Some("extract") => serve_extract(&mut session, &work_dir, &req, limits, out)?,
            _ => request_error(out, None, None, Code::BadRequest, "unknown op")?,
        }
    }
    out.flush()?;
    Ok(EXIT_OK)
}

fn request_error(
    out: &mut Out,
    request_id: Option<&str>,
    message_id: Option<&str>,
    code: Code,
    message: &str,
) -> io::Result<()> {
    out.emit(&RequestErrorEvent {
        kind: "error",
        request_id,
        message_id,
        code: code.as_str(),
        message,
    })?;
    out.flush()
}

fn serve_extract(
    session: &mut Session,
    work_dir: &Path,
    req: &serde_json::Value,
    limits: &Limits,
    out: &mut Out,
) -> io::Result<()> {
    let Some(request_id) = req
        .get("requestId")
        .and_then(|v| v.as_str())
        .filter(|s| valid_request_id(s))
    else {
        return request_error(out, None, None, Code::BadRequest, "invalid requestId");
    };
    let raw_mid = req.get("messageId").and_then(|v| v.as_str());
    let Some(nid) = raw_mid.and_then(parse_message_id) else {
        return request_error(
            out,
            Some(request_id),
            None,
            Code::BadRequest,
            "invalid messageId",
        );
    };
    let mid = message_id(nid);
    // `request_id` cannot contain separators or dots, so this stays a direct
    // child of the work dir; refuse anything pre-existing (incl. symlinks).
    let dir = work_dir.join(request_id);
    if fs::symlink_metadata(&dir).is_ok() {
        return request_error(
            out,
            Some(request_id),
            Some(&mid),
            Code::BadRequest,
            "request directory already exists",
        );
    }
    if let Err(e) = fs::create_dir(&dir) {
        let err = ItemError::io(&e, "cannot create request directory");
        return request_error(out, Some(request_id), Some(&mid), err.code, &err.message);
    }
    let prefix = format!("{request_id}/");
    match extract_into(session, nid, &dir, &prefix, limits) {
        Ok(ex) => {
            out.emit(&ExtractedEvent {
                kind: "extracted",
                request_id: Some(request_id),
                message_id: mid,
                document: format!("{prefix}message.json"),
                document_bytes: ex.document_bytes.len() as u64,
                attachments: ex.files,
                warnings: ex.warnings,
            })?;
            out.flush()
        }
        Err(e) => {
            // Only our own (now emptied) directory is removed.
            let _ = fs::remove_dir(&dir);
            request_error(out, Some(request_id), Some(&mid), e.code, &e.message)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn request_ids() {
        assert!(valid_request_id("r42"));
        assert!(valid_request_id("A_b-9"));
        assert!(!valid_request_id(""));
        assert!(!valid_request_id("../x"));
        assert!(!valid_request_id("a.b"));
        assert!(!valid_request_id(&"x".repeat(65)));
        assert!(valid_request_id(&"x".repeat(64)));
    }

    #[test]
    fn bounded_lines() {
        let data = format!("{}\nok\n", "a".repeat(100));
        let mut r = io::Cursor::new(data.into_bytes());
        assert_eq!(read_bounded_line(&mut r, 10).unwrap(), Some(Err(())));
        assert_eq!(
            read_bounded_line(&mut r, 10).unwrap(),
            Some(Ok("ok\n".into()))
        );
        assert_eq!(read_bounded_line(&mut r, 10).unwrap(), None);
    }
}
