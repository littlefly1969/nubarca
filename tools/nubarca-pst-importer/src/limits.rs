//! Per-invocation limits (contract §2). Defaults match the contract.

#[derive(Clone, Debug)]
pub struct Limits {
    pub max_attachment_bytes: u64,
    pub max_body_bytes: usize,
    pub max_document_bytes: usize,
    pub max_embedded_depth: u32,
    pub max_folder_depth: u32,
    pub max_attachments_per_message: usize,
    /// Sandbox (see `sandbox.rs`): address-space and CPU-time ceilings,
    /// 0 = no limit, and whether a kernel without Landlock is refused.
    pub max_memory_bytes: u64,
    pub max_cpu_seconds: u64,
    pub landlock_required: bool,
    /// When started as root: the unprivileged uid:gid to switch to before
    /// parsing. Ignored when not root (already unprivileged).
    pub run_as: Option<(u32, u32)>,
}

impl Default for Limits {
    fn default() -> Self {
        Limits {
            max_attachment_bytes: 536_870_912,
            max_body_bytes: 16_777_216,
            max_document_bytes: 33_554_432,
            max_embedded_depth: 3,
            max_folder_depth: 64,
            max_attachments_per_message: 500,
            max_memory_bytes: 2_147_483_648,
            max_cpu_seconds: 0,
            landlock_required: false,
            run_as: None,
        }
    }
}

impl Limits {
    /// Apply `--flag value`; returns false when `flag` is not a limit flag.
    pub fn set(&mut self, flag: &str, value: &str) -> Result<bool, String> {
        let parse = || {
            value
                .parse::<u64>()
                .map_err(|_| format!("{flag} expects a non-negative integer"))
        };
        let as_usize = |v: u64| usize::try_from(v).map_err(|_| format!("{flag} is too large"));
        let as_u32 = |v: u64| u32::try_from(v).map_err(|_| format!("{flag} is too large"));
        match flag {
            "--max-attachment-bytes" => self.max_attachment_bytes = parse()?,
            "--max-body-bytes" => self.max_body_bytes = as_usize(parse()?)?,
            "--max-document-bytes" => self.max_document_bytes = as_usize(parse()?)?,
            "--max-embedded-depth" => self.max_embedded_depth = as_u32(parse()?)?,
            "--max-folder-depth" => self.max_folder_depth = as_u32(parse()?)?,
            "--max-attachments-per-message" => {
                self.max_attachments_per_message = as_usize(parse()?)?
            }
            "--max-memory-bytes" => self.max_memory_bytes = parse()?,
            "--max-cpu-seconds" => self.max_cpu_seconds = parse()?,
            "--landlock" => {
                self.landlock_required = match value {
                    "required" => true,
                    "best-effort" => false,
                    _ => return Err("--landlock expects required or best-effort".into()),
                }
            }
            "--run-as" => {
                let ids = value
                    .split_once(':')
                    .and_then(|(u, g)| Some((u.parse::<u32>().ok()?, g.parse::<u32>().ok()?)));
                self.run_as = match ids {
                    Some((0, _)) | Some((_, 0)) => {
                        return Err("--run-as must name a non-root uid:gid".into())
                    }
                    Some(ids) => Some(ids),
                    None => return Err("--run-as expects UID:GID".into()),
                }
            }
            _ => return Ok(false),
        }
        Ok(true)
    }
}
