//! The PST access layer: a thin, bounded adapter over the `outlook-pst` crate.
//!
//! The same implementation (`imp.rs`) is compiled twice, once per on-disk
//! format, with concrete library types; everything above this module works on
//! the format-independent traits defined here.

use std::fs::File;
use std::io::{Read, Write};
use std::path::Path;

use crate::error::{Code, ItemError, ItemResult};
use crate::props::PropMap;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Format {
    Unicode,
    Ansi,
}

impl Format {
    pub fn as_str(self) -> &'static str {
        match self {
            Format::Unicode => "unicode",
            Format::Ansi => "ansi",
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Encryption {
    None,
    Permute,
    Cyclic,
}

impl Encryption {
    pub fn as_str(self) -> &'static str {
        match self {
            Encryption::None => "none",
            Encryption::Permute => "permute",
            Encryption::Cyclic => "cyclic",
        }
    }
}

#[derive(Clone, Copy, Debug)]
pub struct HeaderInfo {
    pub format: Format,
    pub encryption: Encryption,
    pub file_size: u64,
}

/// Recognise a PST from its header bytes (MS-PST HEADER structure) without trusting
/// anything else in the file.
pub fn sniff_header(bytes: &[u8], file_size: u64) -> ItemResult<HeaderInfo> {
    if bytes.len() < 4 || &bytes[0..4] != b"!BDN" {
        return Err(ItemError::new(
            Code::Unsupported,
            "not a PST file (missing !BDN signature)",
        ));
    }
    if bytes.len() < 12 {
        return Err(ItemError::corrupt("truncated PST header"));
    }
    if &bytes[8..10] != b"SM" {
        return Err(ItemError::new(
            Code::Unsupported,
            "unsupported PST client signature",
        ));
    }
    let version = u16::from_le_bytes([bytes[10], bytes[11]]);
    let (format, crypt_offset, header_len) = match version {
        14 | 15 => (Format::Ansi, 0x1CD, 512),
        23 => (Format::Unicode, 0x201, 564),
        36 => {
            return Err(ItemError::new(
                Code::Unsupported,
                "Unicode PST with 4 KiB pages (version 36) is not supported",
            ))
        }
        _ => {
            return Err(ItemError::new(
                Code::Unsupported,
                format!("unsupported PST version {version}"),
            ))
        }
    };
    if bytes.len() < header_len {
        return Err(ItemError::corrupt("truncated PST header"));
    }
    let encryption = match bytes[crypt_offset] {
        0 => Encryption::None,
        1 => Encryption::Permute,
        2 => Encryption::Cyclic,
        other => {
            return Err(ItemError::new(
                Code::Unsupported,
                format!("unsupported PST encryption method 0x{other:02x}"),
            ))
        }
    };
    Ok(HeaderInfo {
        format,
        encryption,
        file_size,
    })
}

/// Read the first bytes of the file (read-only) and recognise the header.
pub fn probe_header(path: &Path) -> ItemResult<HeaderInfo> {
    let file = File::open(path).map_err(|e| open_error(&e))?;
    let file_size = file
        .metadata()
        .map_err(|e| ItemError::io(&e, "cannot stat PST"))?
        .len();
    let mut head = Vec::with_capacity(564);
    file.take(564)
        .read_to_end(&mut head)
        .map_err(|e| ItemError::io(&e, "cannot read PST header"))?;
    sniff_header(&head, file_size)
}

pub fn open_error(e: &std::io::Error) -> ItemError {
    match e.kind() {
        std::io::ErrorKind::NotFound => ItemError::not_found("PST file not found"),
        _ => ItemError::io(e, "cannot open PST"),
    }
}

/// A folder as seen by `list`.
#[derive(Debug, Clone)]
pub struct FolderInfo {
    pub name: Option<String>,
    pub container_class: Option<String>,
    pub content_count: Option<i64>,
    /// NIDs of child folders, in hierarchy-table order.
    pub subfolders: Vec<u32>,
    /// NIDs of messages, in contents-table order.
    pub messages: Vec<u32>,
}

/// A property value read from the file, possibly capped.
#[derive(Debug, Clone)]
pub struct RawProp {
    pub ptype: u16,
    pub bytes: Vec<u8>,
    /// Full stored size of the value (may exceed `bytes.len()`).
    pub total: u64,
}

/// One attachment-table row: the attachment's sub-node id.
#[derive(Debug, Clone, Copy)]
pub struct AttachmentRow {
    pub nid: u32,
}

pub trait Engine {
    fn header(&self) -> HeaderInfo;
    fn store_display_name(&self) -> ItemResult<Option<String>>;
    fn ipm_subtree(&self) -> ItemResult<u32>;
    fn folder(&self, nid: u32) -> ItemResult<FolderInfo>;
    /// Open a top-level message; also returns its parent folder NID.
    fn message(&self, nid: u32) -> ItemResult<(Box<dyn MessageObject>, Option<u32>)>;
}

pub trait MessageObject {
    /// Small properties, decoded; large ones are `PropValue::Deferred`.
    fn props(&self) -> &PropMap;
    /// Read the raw value of a property, at most `cap` bytes of it.
    fn read_prop(&self, id: u16, cap: usize) -> ItemResult<Option<RawProp>>;
    /// Up to `max` recipient rows, and the total row count.
    fn recipients(&self, max: usize) -> ItemResult<(Vec<PropMap>, usize)>;
    fn attachments(&self) -> ItemResult<Vec<AttachmentRow>>;
    fn attachment(&self, row: AttachmentRow) -> ItemResult<Box<dyn AttachmentObject>>;
}

pub trait AttachmentObject {
    fn props(&self) -> &PropMap;
    /// Stored size of the binary data (`PR_ATTACH_DATA_BIN`), if any.
    fn data_size(&self) -> ItemResult<Option<u64>>;
    /// Stream the binary data into `w`; fails with `too_large` past `max`.
    fn write_data(&self, w: &mut dyn Write, max: u64) -> ItemResult<u64>;
    /// Open the embedded message (`PR_ATTACH_DATA_OBJ`).
    fn embedded(&self) -> ItemResult<Box<dyn MessageObject>>;
}

/// Open a PST read-only and return its engine.
pub fn open(path: &Path, validate: bool) -> ItemResult<Box<dyn Engine>> {
    let header = probe_header(path)?;
    match header.format {
        Format::Unicode => Ok(Box::new(unicode::open(path, header, validate)?)),
        Format::Ansi => Ok(Box::new(ansi::open(path, header, validate)?)),
    }
}

mod unicode {
    use outlook_pst::ltp::table_context::UnicodeTableContext as TableCtxT;
    use outlook_pst::messaging::store::UnicodeStore as StoreT;
    use outlook_pst::ndb::block::UnicodeDataTree as DataTreeT;
    use outlook_pst::ndb::block::UnicodeSubNodeTree as SubNodeTreeT;
    use outlook_pst::ndb::block_id::UnicodeBlockId as BlockIdT;
    use outlook_pst::ndb::block_id::UnicodePageId as PageIdT;
    use outlook_pst::ndb::page::UnicodeBlockBTree as BbtT;
    use outlook_pst::ndb::page::UnicodeBlockBTreeEntry as BlockEntryT;
    use outlook_pst::ndb::page::UnicodeNodeBTree as NbtT;
    use outlook_pst::ndb::page::UnicodeNodeBTreeEntry as NodeEntryT;
    use outlook_pst::UnicodePstFile as PstT;
    include!("imp.rs");
}

mod ansi {
    use outlook_pst::ltp::table_context::AnsiTableContext as TableCtxT;
    use outlook_pst::messaging::store::AnsiStore as StoreT;
    use outlook_pst::ndb::block::AnsiDataTree as DataTreeT;
    use outlook_pst::ndb::block::AnsiSubNodeTree as SubNodeTreeT;
    use outlook_pst::ndb::block_id::AnsiBlockId as BlockIdT;
    use outlook_pst::ndb::block_id::AnsiPageId as PageIdT;
    use outlook_pst::ndb::page::AnsiBlockBTree as BbtT;
    use outlook_pst::ndb::page::AnsiBlockBTreeEntry as BlockEntryT;
    use outlook_pst::ndb::page::AnsiNodeBTree as NbtT;
    use outlook_pst::ndb::page::AnsiNodeBTreeEntry as NodeEntryT;
    use outlook_pst::AnsiPstFile as PstT;
    include!("imp.rs");
}

#[cfg(test)]
mod tests {
    use super::*;

    fn header(version: u16, crypt: u8) -> Vec<u8> {
        let mut h = vec![0u8; 564];
        h[0..4].copy_from_slice(b"!BDN");
        h[8..10].copy_from_slice(b"SM");
        h[10..12].copy_from_slice(&version.to_le_bytes());
        let off = if version == 23 { 0x201 } else { 0x1CD };
        h[off] = crypt;
        h
    }

    #[test]
    fn recognises_formats_and_encryption() {
        let u = sniff_header(&header(23, 0), 1).unwrap();
        assert_eq!(
            (u.format, u.encryption),
            (Format::Unicode, Encryption::None)
        );
        let a = sniff_header(&header(14, 1), 1).unwrap();
        assert_eq!(
            (a.format, a.encryption),
            (Format::Ansi, Encryption::Permute)
        );
        let a = sniff_header(&header(15, 2), 1).unwrap();
        assert_eq!((a.format, a.encryption), (Format::Ansi, Encryption::Cyclic));
        assert_eq!(
            sniff_header(&header(36, 0), 1).unwrap_err().code,
            Code::Unsupported
        );
        assert_eq!(
            sniff_header(&header(23, 9), 1).unwrap_err().code,
            Code::Unsupported
        );
        assert_eq!(
            sniff_header(b"PK\x03\x04", 1).unwrap_err().code,
            Code::Unsupported
        );
        assert_eq!(
            sniff_header(&header(23, 0)[..100], 1).unwrap_err().code,
            Code::Corrupt
        );
    }
}
