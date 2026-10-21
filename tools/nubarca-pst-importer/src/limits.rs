//! Per-invocation limits (contract §2). Defaults match the contract.

#[derive(Clone, Debug)]
pub struct Limits {
    pub max_attachment_bytes: u64,
    pub max_body_bytes: usize,
    pub max_document_bytes: usize,
    pub max_embedded_depth: u32,
    pub max_folder_depth: u32,
    pub max_attachments_per_message: usize,
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
            _ => return Ok(false),
        }
        Ok(true)
    }
}
