//! Measurement: how much memory does the `outlook-pst` library itself use to
//! open a large by-value attachment? (The importer does not use this path; it
//! streams attachment data block by block instead.)
//!
//! ```text
//! cargo run --release -p nubarca-pst-fixture-gen -- large /tmp/large.pst 268435456
//! cargo run --release --example measure_library_attachment -- /tmp/large.pst
//! ```

use std::fs::File;
use std::rc::Rc;

use outlook_pst::messaging::attachment::{Attachment, AttachmentData, UnicodeAttachment};
use outlook_pst::messaging::message::{Message, UnicodeMessage};
use outlook_pst::messaging::store::{Store, UnicodeStore};
use outlook_pst::ndb::node_id::NodeId;
use outlook_pst::UnicodePstFile;

fn vm_hwm_kib() -> String {
    std::fs::read_to_string("/proc/self/status")
        .unwrap_or_default()
        .lines()
        .find(|l| l.starts_with("VmHWM:"))
        .map(|l| l.trim_start_matches("VmHWM:").trim().to_string())
        .unwrap_or_else(|| "n/a".into())
}

fn main() -> std::io::Result<()> {
    let path = std::env::args()
        .nth(1)
        .expect("usage: measure_library_attachment PST");
    let pst = Rc::new(UnicodePstFile::read_from(Box::new(File::open(&path)?))?);
    let store = UnicodeStore::read(pst)?;
    println!("after open: VmHWM {}", vm_hwm_kib());
    // The large-attachment fixture has exactly one message, NID 0x200004.
    let entry_id = store
        .properties()
        .make_entry_id(NodeId::from(0x0020_0004))?;
    let message = UnicodeMessage::read(store.clone(), &entry_id, None)?;
    println!("after message: VmHWM {}", vm_hwm_kib());
    let table = message.attachment_table().expect("attachment table");
    let row = table.rows_matrix().next().expect("one attachment");
    let sub_node = NodeId::from(u32::from(row.id()));
    let attachment = UnicodeAttachment::read(message.clone(), sub_node, None)?;
    let size = match attachment.data() {
        Some(AttachmentData::Binary(b)) => b.buffer().len(),
        _ => 0,
    };
    println!("attachment bytes held in memory: {size}");
    println!("after attachment: VmHWM {}", vm_hwm_kib());
    Ok(())
}
