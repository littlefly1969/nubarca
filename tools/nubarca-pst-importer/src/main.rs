//! `nubarca-pst-importer` — read-only PST extraction engine for NubArca
//! MailArchive, speaking protocol `nubarca-pst/1`
//! (see `docs/mailarchive/contract.md` §2).

mod commands;
mod engine;
mod error;
mod hn;
mod limits;
mod model;
mod normalize;
mod props;
mod sandbox;

use std::path::PathBuf;
use std::process::ExitCode;

use limits::Limits;

const USAGE: &str = "\
nubarca-pst-importer — read-only PST engine (protocol nubarca-pst/1)

USAGE:
  nubarca-pst-importer probe   --pst FILE
  nubarca-pst-importer list    --pst FILE
  nubarca-pst-importer serve   --pst FILE --work-dir DIR
  nubarca-pst-importer extract --pst FILE --message m:XXXXXXXX --out-dir DIR
  nubarca-pst-importer sandbox-check --pst FILE --work-dir DIR \
                       --outside-read FILE --outside-dir DIR

LIMITS (every command):
  --max-attachment-bytes N         [536870912]
  --max-body-bytes N               [16777216] per body part
  --max-document-bytes N           [33554432]
  --max-embedded-depth N           [3]
  --max-folder-depth N             [64]
  --max-attachments-per-message N  [500]

SANDBOX (every command; applied before the PST is parsed):
  --max-memory-bytes N             [2147483648] address space, 0 = none
  --max-cpu-seconds N              [0] CPU time, 0 = none
  --landlock required|best-effort  [best-effort]
  --run-as UID:GID                 [none] when root, switch to this user

Output is NDJSON on stdout. Exit codes: 0 ok, 2 fatal input error,
3 usage error, 1 internal error.
";

pub enum Command {
    Probe,
    List,
    Serve {
        work_dir: PathBuf,
    },
    Extract {
        message: String,
        out_dir: PathBuf,
    },
    SandboxCheck {
        work_dir: PathBuf,
        outside_read: PathBuf,
        outside_dir: PathBuf,
    },
}

pub struct Invocation {
    pub command: Command,
    pub pst: PathBuf,
    pub limits: Limits,
}

fn parse_args(args: &[String]) -> Result<Option<Invocation>, String> {
    let Some(cmd) = args.first() else {
        return Err("missing command".into());
    };
    if matches!(cmd.as_str(), "-h" | "--help" | "help") {
        return Ok(None);
    }
    let mut limits = Limits::default();
    let mut pst = None;
    let mut work_dir = None;
    let mut message = None;
    let mut out_dir = None;
    let mut outside_read = None;
    let mut outside_dir = None;
    let mut i = 1;
    while i < args.len() {
        let flag = args[i].as_str();
        let value = args
            .get(i + 1)
            .ok_or_else(|| format!("{flag} expects a value"))?;
        let slot = match flag {
            "--pst" => &mut pst,
            "--work-dir" => &mut work_dir,
            "--message" => &mut message,
            "--out-dir" => &mut out_dir,
            "--outside-read" => &mut outside_read,
            "--outside-dir" => &mut outside_dir,
            _ => {
                if !limits.set(flag, value)? {
                    return Err(format!("unknown option {flag}"));
                }
                i += 2;
                continue;
            }
        };
        if slot.is_some() {
            return Err(format!("{flag} given twice"));
        }
        *slot = Some(value.clone());
        i += 2;
    }
    let pst = PathBuf::from(pst.ok_or("--pst is required")?);
    if cmd != "sandbox-check" && (outside_read.is_some() || outside_dir.is_some()) {
        return Err("--outside-read/--outside-dir belong to sandbox-check".into());
    }
    let command = match cmd.as_str() {
        "probe" | "list" => {
            if work_dir.is_some() || message.is_some() || out_dir.is_some() {
                return Err(format!("{cmd} takes only --pst and limits"));
            }
            if cmd == "probe" {
                Command::Probe
            } else {
                Command::List
            }
        }
        "serve" => {
            if message.is_some() || out_dir.is_some() {
                return Err("serve takes --pst, --work-dir and limits".into());
            }
            Command::Serve {
                work_dir: PathBuf::from(work_dir.ok_or("--work-dir is required")?),
            }
        }
        "extract" => {
            if work_dir.is_some() {
                return Err("extract takes --out-dir, not --work-dir".into());
            }
            Command::Extract {
                message: message.ok_or("--message is required")?,
                out_dir: PathBuf::from(out_dir.ok_or("--out-dir is required")?),
            }
        }
        "sandbox-check" => Command::SandboxCheck {
            work_dir: PathBuf::from(work_dir.ok_or("--work-dir is required")?),
            outside_read: PathBuf::from(outside_read.ok_or("--outside-read is required")?),
            outside_dir: PathBuf::from(outside_dir.ok_or("--outside-dir is required")?),
        },
        other => return Err(format!("unknown command {other}")),
    };
    Ok(Some(Invocation {
        command,
        pst,
        limits,
    }))
}

fn main() -> ExitCode {
    error::install_quiet_panic_hook();
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.len() == 1 && args[0] == "--version" {
        println!(
            "nubarca-pst-importer {} ({})",
            env!("CARGO_PKG_VERSION"),
            model::PROTOCOL
        );
        return ExitCode::SUCCESS;
    }
    let inv = match parse_args(&args) {
        Ok(Some(inv)) => inv,
        Ok(None) => {
            print!("{USAGE}");
            return ExitCode::SUCCESS;
        }
        Err(msg) => {
            eprintln!("nubarca-pst-importer: {msg}\n\n{USAGE}");
            return ExitCode::from(3);
        }
    };
    let peak_rss = open_peak_rss();
    let code = match commands::run(inv) {
        Ok(code) => ExitCode::from(code),
        Err(e) => {
            eprintln!("nubarca-pst-importer: internal error: {e}");
            ExitCode::from(1)
        }
    };
    report_peak_rss(peak_rss);
    code
}

/// Measurement aid: with `NUBARCA_PST_REPORT_PEAK_RSS` set, print the peak
/// resident set size (Linux `VmHWM`) to stderr on exit. The file is opened
/// BEFORE the sandbox (which leaves /proc out of reach) and re-read at exit.
fn open_peak_rss() -> Option<std::fs::File> {
    std::env::var_os("NUBARCA_PST_REPORT_PEAK_RSS")?;
    std::fs::File::open("/proc/self/status").ok()
}

fn report_peak_rss(status: Option<std::fs::File>) {
    use std::io::{Read, Seek, SeekFrom};
    let Some(mut file) = status else {
        return;
    };
    let mut text = String::new();
    if file.seek(SeekFrom::Start(0)).is_err() || file.read_to_string(&mut text).is_err() {
        return;
    }
    if let Some(line) = text.lines().find(|l| l.starts_with("VmHWM:")) {
        let kib = line
            .trim_start_matches("VmHWM:")
            .trim()
            .trim_end_matches("kB")
            .trim();
        eprintln!("peak_rss_kib={kib}");
    }
}
