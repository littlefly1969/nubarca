//! Classified errors. Messages are short, generic and never contain message
//! content (subjects, addresses, bodies, names) — only structural facts.

use std::fmt;
use std::io;

/// Per-item error codes of protocol `nubarca-pst/1` (contract §2.3).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Code {
    NotFound,
    Corrupt,
    TooLarge,
    Unsupported,
    IoError,
    BadRequest,
    LimitExceeded,
    SandboxUnavailable,
}

impl Code {
    pub fn as_str(self) -> &'static str {
        match self {
            Code::NotFound => "not_found",
            Code::Corrupt => "corrupt",
            Code::TooLarge => "too_large",
            Code::Unsupported => "unsupported",
            Code::IoError => "io_error",
            Code::BadRequest => "bad_request",
            Code::LimitExceeded => "limit_exceeded",
            Code::SandboxUnavailable => "sandbox_unavailable",
        }
    }
}

#[derive(Debug, Clone)]
pub struct ItemError {
    pub code: Code,
    pub message: String,
}

impl ItemError {
    pub fn new(code: Code, message: impl Into<String>) -> Self {
        let mut message = message.into();
        // Diagnostics stay short; library error text never carries content,
        // but keep it bounded anyway.
        if message.len() > 240 {
            let mut cut = 240;
            while !message.is_char_boundary(cut) {
                cut -= 1;
            }
            message.truncate(cut);
        }
        ItemError { code, message }
    }

    pub fn corrupt(message: impl Into<String>) -> Self {
        Self::new(Code::Corrupt, message)
    }

    pub fn not_found(message: impl Into<String>) -> Self {
        Self::new(Code::NotFound, message)
    }

    pub fn limit(message: impl Into<String>) -> Self {
        Self::new(Code::LimitExceeded, message)
    }

    pub fn io(err: &io::Error, what: &str) -> Self {
        Self::new(Code::IoError, format!("{what}: {}", io_kind(err)))
    }

    /// Classify an error coming out of the PST library or our own block
    /// walkers. The library reports structural failures as
    /// `InvalidData`/`UnexpectedEof`; a missing B-tree key is `not_found` for
    /// the caller to reinterpret when it means "no such node".
    pub fn from_pst(err: io::Error, what: &str) -> Self {
        match err.kind() {
            io::ErrorKind::InvalidData | io::ErrorKind::UnexpectedEof | io::ErrorKind::Other => {
                let detail = err.to_string();
                Self::corrupt(format!("{what}: {}", sanitize(&detail)))
            }
            _ => Self::io(&err, what),
        }
    }
}

impl fmt::Display for ItemError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}: {}", self.code.as_str(), self.message)
    }
}

fn io_kind(err: &io::Error) -> String {
    format!("{:?}", err.kind())
}

/// Library error strings describe structure (offsets, property types, ids).
/// Strip anything that is not plain printable ASCII and bound the length.
fn sanitize(s: &str) -> String {
    s.chars()
        .filter(|c| c.is_ascii_graphic() || *c == ' ')
        .take(160)
        .collect()
}

/// True when an io::Error from the library means "B-tree key not present".
pub fn is_btree_not_found(err: &io::Error) -> bool {
    err.get_ref()
        .and_then(|inner| inner.downcast_ref::<outlook_pst::ndb::NdbError>())
        .is_some_and(|e| {
            matches!(
                e,
                outlook_pst::ndb::NdbError::BTreePageNotFound(_)
                    | outlook_pst::ndb::NdbError::SubNodeNotFound(_)
            )
        })
}

pub type ItemResult<T> = Result<T, ItemError>;

/// Run `f`, converting a panic inside the PST library (or our parsers) into a
/// classified `corrupt` error. Hostile input must never crash the engine.
pub fn guard<T>(what: &str, f: impl FnOnce() -> ItemResult<T>) -> (ItemResult<T>, bool) {
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(f)) {
        Ok(r) => (r, false),
        Err(_) => (
            Err(ItemError::corrupt(format!(
                "{what}: parser failure on malformed data"
            ))),
            true,
        ),
    }
}

/// Install a panic hook that prints nothing about the payload: panics are
/// caught and reported as classified errors by [`guard`].
pub fn install_quiet_panic_hook() {
    std::panic::set_hook(Box::new(|info| {
        // The source location identifies the parser; the payload is never
        // printed.
        let at = info
            .location()
            .map(|l| format!(" at {}:{}", l.file(), l.line()))
            .unwrap_or_default();
        eprintln!("nubarca-pst-importer: recovered from an internal parser failure{at}");
    }));
}
