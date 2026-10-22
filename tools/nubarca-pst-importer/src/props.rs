//! Format-independent MAPI property values and decoding helpers.

use std::collections::BTreeMap;

// Property types (MS-OXCDATA 2.11.1).
pub const PT_NULL: u16 = 0x0001;
pub const PT_I16: u16 = 0x0002;
pub const PT_I32: u16 = 0x0003;
pub const PT_F32: u16 = 0x0004;
pub const PT_ERROR: u16 = 0x000A;
pub const PT_BOOLEAN: u16 = 0x000B;
pub const PT_OBJECT: u16 = 0x000D;
pub const PT_I64: u16 = 0x0014;
pub const PT_STRING8: u16 = 0x001E;
pub const PT_UNICODE: u16 = 0x001F;
pub const PT_SYSTIME: u16 = 0x0040;
pub const PT_BINARY: u16 = 0x0102;

// Property ids used by the engine.
pub mod id {
    pub const IMPORTANCE: u16 = 0x0017;
    pub const MESSAGE_CLASS: u16 = 0x001A;
    pub const SENSITIVITY: u16 = 0x0036;
    pub const SUBJECT: u16 = 0x0037;
    pub const CLIENT_SUBMIT_TIME: u16 = 0x0039;
    pub const SENT_REPRESENTING_NAME: u16 = 0x0042;
    pub const SENT_REPRESENTING_ADDRTYPE: u16 = 0x0064;
    pub const SENT_REPRESENTING_EMAIL: u16 = 0x0065;
    pub const CONVERSATION_TOPIC: u16 = 0x0070;
    pub const TRANSPORT_HEADERS: u16 = 0x007D;
    pub const RECIPIENT_TYPE: u16 = 0x0C15;
    pub const SENDER_NAME: u16 = 0x0C1A;
    pub const SENDER_ADDRTYPE: u16 = 0x0C1E;
    pub const SENDER_EMAIL: u16 = 0x0C1F;
    pub const MESSAGE_DELIVERY_TIME: u16 = 0x0E06;
    pub const MESSAGE_FLAGS: u16 = 0x0E07;
    pub const ATTACH_SIZE: u16 = 0x0E20;
    pub const BODY: u16 = 0x1000;
    pub const RTF_COMPRESSED: u16 = 0x1009;
    pub const HTML: u16 = 0x1013;
    pub const INTERNET_MESSAGE_ID: u16 = 0x1035;
    pub const REFERENCES: u16 = 0x1039;
    pub const IN_REPLY_TO: u16 = 0x1042;
    pub const DISPLAY_NAME: u16 = 0x3001;
    pub const ADDRTYPE: u16 = 0x3002;
    pub const EMAIL_ADDRESS: u16 = 0x3003;
    pub const CREATION_TIME: u16 = 0x3007;
    pub const LAST_MODIFICATION_TIME: u16 = 0x3008;
    pub const CONTENT_COUNT: u16 = 0x3602;
    pub const CONTAINER_CLASS: u16 = 0x3613;
    pub const ATTACH_DATA: u16 = 0x3701;
    pub const ATTACH_FILENAME: u16 = 0x3704;
    pub const ATTACH_METHOD: u16 = 0x3705;
    pub const ATTACH_LONG_FILENAME: u16 = 0x3707;
    pub const ATTACH_MIME_TAG: u16 = 0x370E;
    pub const ATTACH_CONTENT_ID: u16 = 0x3712;
    pub const ATTACH_FLAGS: u16 = 0x3714;
    pub const SMTP_ADDRESS: u16 = 0x39FE;
    pub const INTERNET_CPID: u16 = 0x3FDE;
    pub const MESSAGE_CODEPAGE: u16 = 0x3FFD;
    pub const SENDER_SMTP_ADDRESS: u16 = 0x5D01;
    pub const SENT_REPRESENTING_SMTP_ADDRESS: u16 = 0x5D02;
    pub const ATTACHMENT_HIDDEN: u16 = 0x7FFE;
}

/// A decoded property value. Values stored outside the property heap (in a
/// sub-node) are `Deferred`: they are read on demand, with a byte cap.
#[derive(Clone, Debug, PartialEq)]
pub enum PropValue {
    Null,
    Int(i64),
    Bool(bool),
    /// FILETIME: 100 ns ticks since 1601-01-01 UTC.
    Time(i64),
    Unicode(String),
    /// Raw 8-bit string; decoded with the owning message's code page.
    String8(Vec<u8>),
    Binary(Vec<u8>),
    Object {
        nid: u32,
        size: u32,
    },
    /// Variable-size value held in sub-node `nid`.
    Deferred {
        ptype: u16,
        nid: u32,
    },
    /// A type the engine does not interpret (floats, GUIDs, multi-values...).
    Other(u16),
}

pub type PropMap = BTreeMap<u16, PropValue>;

/// Decode a fully-read property value of `ptype` from its raw bytes.
pub fn decode_value(ptype: u16, bytes: &[u8]) -> PropValue {
    match ptype {
        PT_NULL => PropValue::Null,
        PT_I16 if bytes.len() >= 2 => {
            PropValue::Int(i16::from_le_bytes([bytes[0], bytes[1]]) as i64)
        }
        PT_I32 | PT_ERROR if bytes.len() >= 4 => {
            PropValue::Int(i32::from_le_bytes(le4(bytes)) as i64)
        }
        PT_BOOLEAN if !bytes.is_empty() => PropValue::Bool(bytes[0] != 0),
        PT_I64 if bytes.len() >= 8 => PropValue::Int(i64::from_le_bytes(le8(bytes))),
        PT_SYSTIME if bytes.len() >= 8 => PropValue::Time(i64::from_le_bytes(le8(bytes))),
        PT_UNICODE => PropValue::Unicode(utf16le_to_string(bytes)),
        PT_STRING8 => PropValue::String8(bytes.to_vec()),
        PT_BINARY => PropValue::Binary(bytes.to_vec()),
        PT_OBJECT if bytes.len() >= 8 => PropValue::Object {
            nid: u32::from_le_bytes(le4(bytes)),
            size: u32::from_le_bytes(le4(&bytes[4..])),
        },
        other => PropValue::Other(other),
    }
}

fn le4(b: &[u8]) -> [u8; 4] {
    [b[0], b[1], b[2], b[3]]
}

fn le8(b: &[u8]) -> [u8; 8] {
    [b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7]]
}

/// UTF-16LE to UTF-8 (lossy), dropping an odd trailing byte and trailing NULs.
pub fn utf16le_to_string(bytes: &[u8]) -> String {
    let units: Vec<u16> = bytes
        .chunks_exact(2)
        .map(|c| u16::from_le_bytes([c[0], c[1]]))
        .collect();
    let mut s = String::from_utf16_lossy(&units);
    trim_nuls(&mut s);
    s
}

pub fn trim_nuls(s: &mut String) {
    while s.ends_with('\0') {
        s.pop();
    }
}

/// Decode 8-bit text in Windows code page `cp` (lossy). Unknown code pages
/// fall back to Windows-1252.
pub fn decode_codepage(bytes: &[u8], cp: Option<u32>) -> String {
    let enc = cp
        .and_then(|cp| u16::try_from(cp).ok())
        .and_then(codepage::to_encoding)
        .unwrap_or(encoding_rs::WINDOWS_1252);
    let (text, _, _) = enc.decode(bytes);
    let mut s = text.into_owned();
    trim_nuls(&mut s);
    s
}

/// True when `cp` maps to an encoding we know.
pub fn known_codepage(cp: u32) -> bool {
    u16::try_from(cp)
        .ok()
        .and_then(codepage::to_encoding)
        .is_some()
}

/// FILETIME ticks to ISO-8601 UTC with second precision, or `None` when out
/// of the representable 1601..=9999 range.
pub fn filetime_to_iso(ticks: i64) -> Option<String> {
    if ticks <= 0 {
        return None;
    }
    // 11644473600 seconds between 1601-01-01 and 1970-01-01.
    let secs_1601 = ticks / 10_000_000;
    let unix = secs_1601 - 11_644_473_600;
    let days = unix.div_euclid(86_400);
    let sod = unix.rem_euclid(86_400);
    let (y, m, d) = civil_from_days(days);
    if !(1601..=9999).contains(&y) {
        return None;
    }
    Some(format!(
        "{y:04}-{m:02}-{d:02}T{:02}:{:02}:{:02}Z",
        sod / 3600,
        (sod % 3600) / 60,
        sod % 60
    ))
}

/// Howard Hinnant's days-from-civil inverse (proleptic Gregorian).
fn civil_from_days(z: i64) -> (i64, u32, u32) {
    let z = z + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (if m <= 2 { y + 1 } else { y }, m, d)
}

/// Truncate a string to at most `max` UTF-8 bytes on a char boundary.
/// Returns true when something was cut.
pub fn truncate_utf8(s: &mut String, max: usize) -> bool {
    if s.len() <= max {
        return false;
    }
    let mut cut = max;
    while cut > 0 && !s.is_char_boundary(cut) {
        cut -= 1;
    }
    s.truncate(cut);
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn filetime_conversion() {
        // 2024-03-01T10:00:00Z
        let unix = 1_709_287_200i64;
        let ticks = (unix + 11_644_473_600) * 10_000_000;
        assert_eq!(filetime_to_iso(ticks).unwrap(), "2024-03-01T10:00:00Z");
        assert_eq!(filetime_to_iso(0), None);
        assert_eq!(filetime_to_iso(i64::MAX), None);
        // Epoch of FILETIME itself.
        assert_eq!(filetime_to_iso(1).unwrap(), "1601-01-01T00:00:00Z");
    }

    #[test]
    fn utf16_and_codepages() {
        let s = "Perché ß Ωμέγα 日本 😀";
        let bytes: Vec<u8> = s
            .encode_utf16()
            .flat_map(|u| u.to_le_bytes())
            .chain([0, 0])
            .collect();
        assert_eq!(utf16le_to_string(&bytes), s);
        assert_eq!(decode_codepage(b"caf\xe9", Some(1252)), "café");
        assert_eq!(decode_codepage(b"\xe1\xe2", Some(1253)), "αβ");
        assert_eq!(decode_codepage(b"caf\xe9", None), "café");
        assert_eq!(decode_codepage("日本".as_bytes(), Some(65001)), "日本");
    }

    #[test]
    fn truncation_respects_char_boundaries() {
        let mut s = "aé😀".to_string();
        assert!(truncate_utf8(&mut s, 4));
        assert_eq!(s, "aé");
        let mut t = "abc".to_string();
        assert!(!truncate_utf8(&mut t, 3));
    }

    #[test]
    fn decode_values() {
        assert_eq!(decode_value(PT_I32, &7i32.to_le_bytes()), PropValue::Int(7));
        assert_eq!(decode_value(PT_I32, &[1]), PropValue::Other(PT_I32));
        assert_eq!(
            decode_value(PT_OBJECT, &[1, 0, 0, 0, 2, 0, 0, 0]),
            PropValue::Object { nid: 1, size: 2 }
        );
    }
}
