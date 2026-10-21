//! Serialized shapes of protocol `nubarca-pst/1` (contract §2) and of the
//! normalized message document `nubarca-mail-message` v1 (§2.4). Field order
//! follows the contract.

use serde::Serialize;

pub const PROTOCOL: &str = "nubarca-pst/1";
pub const DOCUMENT_FORMAT: &str = "nubarca-mail-message";
pub const DOCUMENT_VERSION: u32 = 1;

pub fn folder_id(nid: u32) -> String {
    format!("f:{nid:08x}")
}

pub fn message_id(nid: u32) -> String {
    format!("m:{nid:08x}")
}

/// Parse `m:` + 8 lowercase hex digits.
pub fn parse_message_id(s: &str) -> Option<u32> {
    let hex = s.strip_prefix("m:")?;
    if hex.len() != 8
        || !hex
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
    {
        return None;
    }
    u32::from_str_radix(hex, 16).ok()
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProbeEvent<'a> {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub protocol: &'static str,
    pub format: &'static str,
    pub encryption: &'static str,
    pub store_display_name: Option<&'a str>,
    pub file_size: u64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FolderEvent<'a> {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub folder_id: String,
    pub parent_folder_id: Option<String>,
    pub name: Option<&'a str>,
    pub path: &'a [String],
    pub depth: u32,
    pub container_class: Option<&'a str>,
    pub content_count: Option<i64>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MessageEvent {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub message_id: String,
    pub folder_id: String,
}

#[derive(Serialize)]
pub struct ListErrorEvent<'a> {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub scope: &'static str,
    pub id: String,
    pub code: &'static str,
    pub message: &'a str,
}

#[derive(Serialize)]
pub struct EndEvent {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub folders: u64,
    pub messages: u64,
    pub errors: u64,
}

#[derive(Serialize)]
pub struct FatalEvent<'a> {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub code: &'static str,
    pub message: &'a str,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExtractedEvent<'a> {
    #[serde(rename = "type")]
    pub kind: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub request_id: Option<&'a str>,
    pub message_id: String,
    pub document: String,
    pub document_bytes: u64,
    pub attachments: Vec<ExtractedFile>,
    pub warnings: Vec<Warning>,
}

#[derive(Serialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct ExtractedFile {
    pub key: String,
    pub file: String,
    pub size_bytes: u64,
    pub sha256: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RequestErrorEvent<'a> {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub request_id: Option<&'a str>,
    pub message_id: Option<&'a str>,
    pub code: &'static str,
    pub message: &'a str,
}

#[derive(Serialize, Clone, Debug, PartialEq)]
pub struct Warning {
    pub code: &'static str,
    pub message: String,
}

#[derive(Serialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Source {
    pub message_id: String,
    pub folder_id: Option<String>,
}

/// The top-level normalized document.
#[derive(Serialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct MessageDocument {
    pub format: &'static str,
    pub version: u32,
    pub source: Source,
    #[serde(flatten)]
    pub content: MessageContent,
}

/// Fields shared by a message and an embedded message.
#[derive(Serialize, Debug, Default)]
#[serde(rename_all = "camelCase")]
pub struct MessageContent {
    pub message_class: Option<String>,
    pub subject: Option<String>,
    pub conversation_topic: Option<String>,
    pub from: Option<Address>,
    pub sender: Option<Address>,
    pub to: Vec<Address>,
    pub cc: Vec<Address>,
    pub bcc: Vec<Address>,
    pub sent_at: Option<String>,
    pub received_at: Option<String>,
    pub created_at: Option<String>,
    pub modified_at: Option<String>,
    pub internet_message_id: Option<String>,
    pub in_reply_to: Option<String>,
    pub references: Option<String>,
    pub importance: Option<i64>,
    pub sensitivity: Option<i64>,
    pub flags: Option<i64>,
    pub has_attachments: bool,
    pub transport_headers: Option<String>,
    pub body: Body,
    pub attachments: Vec<AttachmentDoc>,
    pub warnings: Vec<Warning>,
}

#[derive(Serialize, Debug, Clone, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Address {
    pub name: Option<String>,
    pub address: Option<String>,
    pub address_type: Option<String>,
}

#[derive(Serialize, Debug, Default)]
pub struct Body {
    pub text: Option<String>,
    pub html: Option<String>,
    pub rtf: Option<String>,
    pub truncated: Truncated,
}

#[derive(Serialize, Debug, Default, Clone, Copy)]
pub struct Truncated {
    pub text: bool,
    pub html: bool,
    pub rtf: bool,
}

#[derive(Serialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct AttachmentDoc {
    pub key: String,
    pub method: &'static str,
    pub file_name: Option<String>,
    pub display_name: Option<String>,
    pub mime_type: Option<String>,
    pub content_id: Option<String>,
    pub inline: bool,
    pub hidden: bool,
    pub size_bytes: Option<u64>,
    pub extracted: bool,
    pub skip_reason: Option<&'static str>,
    pub embedded_message: Option<Box<MessageContent>>,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ids_roundtrip() {
        assert_eq!(message_id(0x200024), "m:00200024");
        assert_eq!(folder_id(0x8022), "f:00008022");
        assert_eq!(parse_message_id("m:00200024"), Some(0x200024));
        assert_eq!(parse_message_id("m:0020002"), None);
        assert_eq!(parse_message_id("m:0020002A"), None);
        assert_eq!(parse_message_id("f:00200024"), None);
        assert_eq!(parse_message_id("m:+0200024"), None);
    }
}
