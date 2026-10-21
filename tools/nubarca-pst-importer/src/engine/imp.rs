// Format-specific engine implementation. This file is `include!`d by
// `engine/mod.rs` twice, inside the `unicode` and `ansi` modules, which bring
// the concrete `outlook-pst` types into scope under the aliases used below
// (`PstT`, `StoreT`, `BlockIdT`, ...). Writing it once against aliases avoids
// restating the library's crate-private trait bounds.

use std::cell::RefCell;
use std::collections::{BTreeMap, BTreeSet};
use std::fs::File;
use std::io::{self, Write};
use std::path::Path;
use std::rc::Rc;

use outlook_pst::ltp::prop_context::PropertyValue;
use outlook_pst::ltp::prop_type::PropertyType;
use outlook_pst::ltp::table_context::TableContext;
use outlook_pst::messaging::store::Store;
use outlook_pst::ndb::block::{
    Block, DataTree, IntermediateDataTreeEntry, IntermediateTreeBlock, IntermediateTreeHeader,
    SubNodeTree,
};
use outlook_pst::ndb::block_id::BlockId;
use outlook_pst::ndb::block_ref::BlockRef;
use outlook_pst::ndb::byte_index::ByteIndex;
use outlook_pst::ndb::header::Header;
use outlook_pst::ndb::node_id::NodeId;
use outlook_pst::ndb::page::{
    BTreePage, BTreePageEntry, BlockBTreeEntry, NodeBTreeEntry, RootBTreePage,
};
use outlook_pst::ndb::root::Root;
use outlook_pst::PstFile;

use super::{
    AttachmentObject, AttachmentRow, Engine, FolderInfo, HeaderInfo, MessageObject, RawProp,
};
use crate::error::{is_btree_not_found, Code, ItemError, ItemResult};
use crate::hn::{pc_value, read_pc, Heap, PcRecord};
use crate::props::{
    decode_codepage, id, PropMap, PropValue, PT_BINARY, PT_OBJECT,
};

/// Largest property-context heap we load (PC heaps hold only values below
/// ~3.5 KiB each; real ones are a few KiB).
const MAX_HEAP_BYTES: u64 = 16 << 20;
/// Largest table (heap + row matrix + other sub-nodes) handed to the library,
/// which materialises all rows.
const MAX_TABLE_BYTES: u64 = 256 << 20;
/// Largest number of sub-nodes per node.
const MAX_SUBNODES: usize = 1 << 20;

const NID_TYPE_MASK: u32 = 0x1F;
const NID_TYPE_NORMAL_FOLDER: u32 = 0x02;
const NID_TYPE_NORMAL_MESSAGE: u32 = 0x04;
const NID_TYPE_ATTACHMENT: u32 = 0x05;
const NID_TYPE_HIERARCHY_TABLE: u32 = 0x0D;
const NID_TYPE_CONTENTS_TABLE: u32 = 0x0E;
const NID_TYPE_ATTACHMENT_TABLE: u32 = 0x11;
const NID_TYPE_RECIPIENT_TABLE: u32 = 0x12;
const NID_MESSAGE_STORE: u32 = 0x21;

type SubNodes = BTreeMap<u32, (BlockIdT, Option<BlockIdT>)>;

// The conversion is a no-op for the Unicode instantiation but widens u32 for ANSI.
#[allow(clippy::useless_conversion)]
fn bid_key(b: BlockIdT) -> u64 {
    b.search_key().into()
}

fn pst_err(what: &'static str) -> impl Fn(io::Error) -> ItemError {
    move |e| ItemError::from_pst(e, what)
}

struct Core {
    header: HeaderInfo,
    store: Rc<StoreT>,
    b: Blocks,
}

pub struct EngineImpl {
    core: Rc<Core>,
}

pub fn open(path: &Path, header: HeaderInfo, validate: bool) -> ItemResult<EngineImpl> {
    // Read-only handles only. `PstT::open` would also open the file for
    // writing, so it is never used.
    let lib_file = File::open(path).map_err(|e| super::open_error(&e))?;
    let pst = PstT::read_from(Box::new(lib_file)).map_err(pst_err("PST header unreadable"))?;
    let pst = Rc::new(pst);
    let mut file = File::open(path).map_err(|e| super::open_error(&e))?;
    let (nbt_ref, bbt_ref, encoding) = {
        let h = pst.header();
        (*h.root().node_btree(), *h.root().block_btree(), h.crypt_method())
    };
    if validate {
        validate_btrees(&mut file, nbt_ref, bbt_ref, header.file_size)?;
    }
    let nbt = NbtT::read(&mut file, nbt_ref).map_err(pst_err("node B-tree unreadable"))?;
    let bbt = BbtT::read(&mut file, bbt_ref).map_err(pst_err("block B-tree unreadable"))?;

    // The store's property context is read by the library: bound it first.
    let blocks = Blocks {
        file: RefCell::new(file),
        nbt,
        bbt,
        ncache: Default::default(),
        bcache: Default::default(),
        encoding,
    };
    let store_node = blocks.node(NID_MESSAGE_STORE)?;
    blocks.check_node_budget(store_node.data(), store_node.sub_node(), MAX_HEAP_BYTES)?;
    let store = StoreT::read(pst.clone()).map_err(pst_err("message store unreadable"))?;
    Ok(EngineImpl {
        core: Rc::new(Core {
            header,
            store,
            b: blocks,
        }),
    })
}

/// Bounded block-level access: B-tree lookups, data trees, sub-node trees.
struct Blocks {
    file: RefCell<File>,
    nbt: NbtT,
    bbt: BbtT,
    ncache: RefCell<BTreeMap<PageIdT, NbtT>>,
    bcache: RefCell<BTreeMap<PageIdT, BbtT>>,
    encoding: outlook_pst::ndb::header::NdbCryptMethod,
}

/// Walk both B-trees once: levels must strictly decrease from parent to
/// child, no page may be reached twice and the page count is bounded by the
/// file size. This makes every later library lookup (which recurses without
/// such checks) provably finite.
fn validate_btrees(
    f: &mut File,
    nbt_ref: <PstT as PstFile>::PageRef,
    bbt_ref: <PstT as PstFile>::PageRef,
    file_size: u64,
) -> ItemResult<()> {
    let max_pages = file_size / 512 + 1;
    macro_rules! walk {
        ($tree:ty, $root:expr, $what:literal) => {{
            let mut seen: BTreeSet<u64> = BTreeSet::new();
            let mut stack = vec![($root, None::<u8>)];
            while let Some((page_ref, expected)) = stack.pop() {
                let offset: u64 = page_ref.index().index().into();
                if offset >= file_size || !seen.insert(offset) {
                    return Err(ItemError::corrupt(concat!($what, ": page loop or bad offset")));
                }
                if seen.len() as u64 > max_pages {
                    return Err(ItemError::corrupt(concat!($what, ": too many pages")));
                }
                let page = <$tree>::read(f, page_ref).map_err(pst_err($what))?;
                match page {
                    RootBTreePage::Intermediate(p, _) => {
                        let level = p.level();
                        if expected.is_some_and(|e| e != level) || level == 0 {
                            return Err(ItemError::corrupt(concat!($what, ": inconsistent levels")));
                        }
                        for entry in p.entries() {
                            stack.push((entry.block(), Some(level - 1)));
                        }
                    }
                    RootBTreePage::Leaf(_) => {
                        if expected.is_some_and(|e| e != 0) {
                            return Err(ItemError::corrupt(concat!($what, ": inconsistent levels")));
                        }
                    }
                }
            }
        }};
    }
    walk!(NbtT, nbt_ref, "node B-tree");
    walk!(BbtT, bbt_ref, "block B-tree");
    Ok(())
}

impl Blocks {
            fn node(&self, nid: u32) -> ItemResult<NodeEntryT> {
                let mut f = self.file.borrow_mut();
                let mut cache = self.ncache.borrow_mut();
                self.nbt
                    .find_entry(&mut *f, NodeId::from(nid).into_key(), &mut cache)
                    .map_err(|e| {
                        if is_btree_not_found(&e) {
                            ItemError::not_found("no such node")
                        } else {
                            ItemError::from_pst(e, "node lookup failed")
                        }
                    })
            }

            fn block_entry(&self, bid: BlockIdT) -> ItemResult<BlockEntryT> {
                let mut f = self.file.borrow_mut();
                let mut cache = self.bcache.borrow_mut();
                self.bbt
                    .find_entry(&mut *f, bid.search_key(), &mut cache)
                    .map_err(|e| {
                        if is_btree_not_found(&e) {
                            ItemError::corrupt("referenced block missing")
                        } else {
                            ItemError::from_pst(e, "block lookup failed")
                        }
                    })
            }

            fn read_tree_block(&self, bid: BlockIdT) -> ItemResult<DataTreeT> {
                let entry = self.block_entry(bid)?;
                let mut f = self.file.borrow_mut();
                DataTree::read(&mut *f, self.encoding, &entry).map_err(pst_err("data block unreadable"))
            }

            /// Stored size of a data tree, from its root block only.
            fn tree_size(&self, bid: BlockIdT) -> ItemResult<u64> {
                if !bid.is_internal() {
                    return Ok(self.block_entry(bid)?.size() as u64);
                }
                match self.read_tree_block(bid)? {
                    DataTree::Intermediate(b) => Ok(b.header().total_size() as u64),
                    DataTree::Leaf(_) => Err(ItemError::corrupt("internal block id on leaf")),
                }
            }

            /// Leaf block ids of a data tree, validating its shape
            /// (XXBLOCK -> XBLOCK -> data blocks, nothing deeper).
            fn tree_leaves(&self, bid: BlockIdT) -> ItemResult<Vec<BlockIdT>> {
                if !bid.is_internal() {
                    return Ok(vec![bid]);
                }
                let mut leaves = Vec::new();
                let mut seen = BTreeSet::new();
                let mut stack = vec![(bid, None::<u8>)];
                while let Some((b, expected)) = stack.pop() {
                    if !seen.insert(bid_key(b)) {
                        return Err(ItemError::corrupt("data tree loop"));
                    }
                    let DataTree::Intermediate(block) = self.read_tree_block(b)? else {
                        return Err(ItemError::corrupt("data tree shape invalid"));
                    };
                    let level = block.header().level();
                    if !(1..=2).contains(&level) || expected.is_some_and(|e| e != level) {
                        return Err(ItemError::corrupt("data tree level invalid"));
                    }
                    if level == 2 {
                        // Children are XBLOCKs: visit them in order (stack).
                        for entry in block.entries().iter().rev() {
                            let child = entry.block();
                            if !child.is_internal() {
                                return Err(ItemError::corrupt("data tree shape invalid"));
                            }
                            stack.push((child, Some(1)));
                        }
                    } else {
                        for entry in block.entries() {
                            let child = entry.block();
                            if child.is_internal() {
                                return Err(ItemError::corrupt("data tree shape invalid"));
                            }
                            leaves.push(child);
                        }
                    }
                }
                Ok(leaves)
            }

            /// Visit the decoded leaf blocks of a data tree in order until
            /// `visit` returns false.
            fn for_each_leaf(
                &self,
                bid: BlockIdT,
                mut visit: impl FnMut(&[u8]) -> ItemResult<bool>,
            ) -> ItemResult<()> {
                for leaf in self.tree_leaves(bid)? {
                    match self.read_tree_block(leaf)? {
                        DataTree::Leaf(block) => {
                            if !visit(block.data())? {
                                break;
                            }
                        }
                        DataTree::Intermediate(_) => {
                            return Err(ItemError::corrupt("data tree shape invalid"))
                        }
                    }
                }
                Ok(())
            }

            /// Read up to `cap` bytes of a data tree; returns (bytes, stored size).
            fn read_tree(&self, bid: BlockIdT, cap: usize) -> ItemResult<(Vec<u8>, u64)> {
                let total = self.tree_size(bid)?;
                let mut out = Vec::with_capacity((total as usize).min(cap).min(1 << 20));
                self.for_each_leaf(bid, |data| {
                    let room = cap - out.len();
                    out.extend_from_slice(&data[..data.len().min(room)]);
                    Ok(out.len() < cap)
                })?;
                Ok((out, total))
            }

            /// Heap blocks of a data tree, bounded.
            fn heap(&self, bid: BlockIdT) -> ItemResult<Heap> {
                if self.tree_size(bid)? > MAX_HEAP_BYTES {
                    return Err(ItemError::limit("property heap exceeds engine limit"));
                }
                let mut blocks = Vec::new();
                self.for_each_leaf(bid, |data| {
                    blocks.push(data.to_vec());
                    Ok(true)
                })?;
                Heap::new(blocks)
            }

            /// All sub-node entries below `sub` (SIBLOCK -> SLBLOCK at most).
            fn subnodes(&self, sub: Option<BlockIdT>) -> ItemResult<SubNodes> {
                let mut out = SubNodes::new();
                let Some(sub) = sub else {
                    return Ok(out);
                };
                let mut stack = vec![(sub, true)];
                let mut seen = BTreeSet::new();
                while let Some((b, may_be_intermediate)) = stack.pop() {
                    if !seen.insert(bid_key(b)) {
                        return Err(ItemError::corrupt("sub-node tree loop"));
                    }
                    let entry = self.block_entry(b)?;
                    let tree = {
                        let mut f = self.file.borrow_mut();
                        SubNodeTreeT::read(&mut *f, &entry)
                            .map_err(pst_err("sub-node tree unreadable"))?
                    };
                    match tree {
                        SubNodeTree::Intermediate(block) => {
                            if !may_be_intermediate || block.header().level() != 1 {
                                return Err(ItemError::corrupt("sub-node tree level invalid"));
                            }
                            for e in block.entries().iter().rev() {
                                stack.push((e.block(), false));
                            }
                        }
                        SubNodeTree::Leaf(block) => {
                            for e in block.entries() {
                                out.insert(u32::from(e.node()), (e.block(), e.sub_node()));
                                if out.len() > MAX_SUBNODES {
                                    return Err(ItemError::limit("too many sub-nodes"));
                                }
                            }
                        }
                    }
                }
                Ok(out)
            }

            /// Validate the shapes of a node's data tree and of every sub-node
            /// data tree, and bound their total stored size, before handing the
            /// node to a library reader.
            fn check_node_budget(
                &self,
                data: BlockIdT,
                sub: Option<BlockIdT>,
                max: u64,
            ) -> ItemResult<()> {
                let mut total = self.tree_size(data)?;
                self.tree_leaves(data)?;
                for (_, (d, s)) in self.subnodes(sub)? {
                    total = total.saturating_add(self.tree_size(d)?);
                    if total > max {
                        return Err(ItemError::limit("table exceeds engine size limit"));
                    }
                    self.tree_leaves(d)?;
                    if s.is_some() {
                        // Nested sub-node trees are never read by the table
                        // reader; validate their shape anyway.
                        self.subnodes(s)?;
                    }
                }
                if total > max {
                    return Err(ItemError::limit("table exceeds engine size limit"));
                }
                Ok(())
            }
}

trait IntoKey {
    fn into_key(self) -> <PstT as PstFile>::BTreeKey;
}

impl IntoKey for NodeId {
    // No-op for ANSI (u32 keys), widening for Unicode (u64 keys).
    #[allow(clippy::useless_conversion)]
    fn into_key(self) -> <PstT as PstFile>::BTreeKey {
        u32::from(self).into()
    }
}


/// A property context loaded from a node: its heap, records, decoded small
/// values and sub-nodes.
struct PcObject {
    heap: Heap,
    records: BTreeMap<u16, PcRecord>,
    props: PropMap,
    subnodes: Rc<SubNodes>,
}

impl std::ops::Deref for Core {
    type Target = Blocks;

    fn deref(&self) -> &Blocks {
        &self.b
    }
}

impl Core {
    fn pc(&self, data: BlockIdT, sub: Option<BlockIdT>) -> ItemResult<PcObject> {
        let heap = self.heap(data)?;
        let mut records = BTreeMap::new();
        let mut props = PropMap::new();
        for rec in read_pc(&heap)? {
            let value = pc_value(&heap, &rec)?;
            records.insert(rec.prop_id, rec);
            props.insert(rec.prop_id, value);
        }
        let subnodes = Rc::new(self.subnodes(sub)?);
        Ok(PcObject {
            heap,
            records,
            props,
            subnodes,
        })
    }

    fn read_value(&self, pc: &PcObject, prop: u16, cap: usize) -> ItemResult<Option<RawProp>> {
        let Some(rec) = pc.records.get(&prop) else {
            return Ok(None);
        };
        let hnid = rec.hnid;
        if hnid == 0 {
            return Ok(Some(RawProp {
                ptype: rec.ptype,
                bytes: Vec::new(),
                total: 0,
            }));
        }
        if hnid & NID_TYPE_MASK == 0 {
            let bytes = pc.heap.get(hnid)?;
            let total = bytes.len() as u64;
            return Ok(Some(RawProp {
                ptype: rec.ptype,
                bytes: bytes[..bytes.len().min(cap)].to_vec(),
                total,
            }));
        }
        let (data, _) = pc
            .subnodes
            .get(&hnid)
            .copied()
            .ok_or_else(|| ItemError::corrupt("property sub-node missing"))?;
        let (bytes, total) = self.read_tree(data, cap)?;
        Ok(Some(RawProp {
            ptype: rec.ptype,
            bytes,
            total,
        }))
    }

    fn table(&self, nid: u32, data: BlockIdT, sub: Option<BlockIdT>) -> ItemResult<Rc<dyn TableContext>> {
        self.check_node_budget(data, sub, MAX_TABLE_BYTES)?;
        let node = NodeEntryT::new(NodeId::from(nid), data, sub, None);
        TableCtxT::read(self.store.clone(), node).map_err(pst_err("table unreadable"))
    }

    /// Row ids of a table (a hierarchy/contents/attachment table's rows are
    /// keyed by the child node id).
    fn table_row_ids(&self, tc: &Rc<dyn TableContext>) -> Vec<u32> {
        tc.rows_matrix().map(|row| u32::from(row.id())).collect()
    }

    /// Up to `max` rows of a table converted to property maps.
    fn table_rows(&self, tc: &Rc<dyn TableContext>, max: usize) -> ItemResult<(Vec<PropMap>, usize)> {
        let context = tc.context();
        let mut rows = Vec::new();
        let mut total = 0usize;
        for row in tc.rows_matrix() {
            total += 1;
            if rows.len() >= max {
                continue;
            }
            let values = row.columns(context).map_err(pst_err("table row unreadable"))?;
            let mut map = PropMap::new();
            for (col, value) in context.columns().iter().zip(values) {
                let Some(value) = value else { continue };
                let v = tc
                    .read_column(&value, col.prop_type())
                    .map_err(pst_err("table cell unreadable"))?;
                map.insert(col.prop_id(), convert(v));
            }
            rows.push(map);
        }
        Ok((rows, total))
    }

    fn message_from(&self, data: BlockIdT, sub: Option<BlockIdT>, core: &Rc<Core>) -> ItemResult<MessageImpl> {
        let pc = self.pc(data, sub)?;
        Ok(MessageImpl {
            core: core.clone(),
            pc,
        })
    }
}

fn convert(v: PropertyValue) -> PropValue {
    match v {
        PropertyValue::Null => PropValue::Null,
        PropertyValue::Integer16(i) => PropValue::Int(i as i64),
        PropertyValue::Integer32(i) => PropValue::Int(i as i64),
        PropertyValue::ErrorCode(i) => PropValue::Int(i as i64),
        PropertyValue::Integer64(i) => PropValue::Int(i),
        PropertyValue::Boolean(b) => PropValue::Bool(b),
        PropertyValue::Time(t) => PropValue::Time(t),
        PropertyValue::String8(s) => PropValue::String8(s.buffer().to_vec()),
        PropertyValue::Unicode(u) => {
            let mut s = String::from_utf16_lossy(u.buffer());
            crate::props::trim_nuls(&mut s);
            PropValue::Unicode(s)
        }
        PropertyValue::Binary(b) => PropValue::Binary(b.buffer().to_vec()),
        PropertyValue::Object(o) => PropValue::Object {
            nid: u32::from(o.node()),
            size: o.size(),
        },
        other => PropValue::Other(u16::from(PropertyType::from(&other))),
    }
}

fn string_prop(props: &PropMap, prop: u16) -> Option<String> {
    match props.get(&prop)? {
        PropValue::Unicode(s) => Some(s.clone()),
        PropValue::String8(b) => Some(decode_codepage(b, None)),
        _ => None,
    }
}

impl Engine for EngineImpl {
    fn header(&self) -> HeaderInfo {
        self.core.header
    }

    fn store_display_name(&self) -> ItemResult<Option<String>> {
        Ok(match self.core.store.properties().get(id::DISPLAY_NAME) {
            Some(v) => match convert(v.clone()) {
                PropValue::Unicode(s) => Some(s),
                PropValue::String8(b) => Some(decode_codepage(&b, None)),
                _ => None,
            },
            None => None,
        })
    }

    fn ipm_subtree(&self) -> ItemResult<u32> {
        let entry = self
            .core
            .store
            .properties()
            .ipm_sub_tree_entry_id()
            .map_err(pst_err("IPM subtree entry id unreadable"))?;
        Ok(u32::from(entry.node_id()))
    }

    fn folder(&self, nid: u32) -> ItemResult<FolderInfo> {
        let core = &self.core;
        if nid & NID_TYPE_MASK != NID_TYPE_NORMAL_FOLDER {
            return Err(ItemError::new(Code::Unsupported, "not a normal folder"));
        }
        let node = core.node(nid)?;
        let pc = core.pc(node.data(), node.sub_node())?;
        let content_count = match pc.props.get(&id::CONTENT_COUNT) {
            Some(PropValue::Int(n)) => Some(*n),
            _ => None,
        };
        let index = nid & !NID_TYPE_MASK;
        let mut subfolders = Vec::new();
        match core.node(index | NID_TYPE_HIERARCHY_TABLE) {
            Ok(t) => {
                let tc = core.table(index | NID_TYPE_HIERARCHY_TABLE, t.data(), t.sub_node())?;
                subfolders = core.table_row_ids(&tc);
            }
            Err(e) if e.code == Code::NotFound => {}
            Err(e) => return Err(e),
        }
        let mut messages = Vec::new();
        match core.node(index | NID_TYPE_CONTENTS_TABLE) {
            Ok(t) => {
                let tc = core.table(index | NID_TYPE_CONTENTS_TABLE, t.data(), t.sub_node())?;
                messages = core.table_row_ids(&tc);
            }
            Err(e) if e.code == Code::NotFound => {}
            Err(e) => return Err(e),
        }
        Ok(FolderInfo {
            name: string_prop(&pc.props, id::DISPLAY_NAME),
            container_class: string_prop(&pc.props, id::CONTAINER_CLASS),
            content_count,
            subfolders,
            messages,
        })
    }

    fn message(&self, nid: u32) -> ItemResult<(Box<dyn MessageObject>, Option<u32>)> {
        if nid & NID_TYPE_MASK != NID_TYPE_NORMAL_MESSAGE {
            return Err(ItemError::not_found("no such message"));
        }
        let node = match self.core.node(nid) {
            Ok(n) => n,
            Err(e) if e.code == Code::NotFound => return Err(ItemError::not_found("no such message")),
            Err(e) => return Err(e),
        };
        let parent = node.parent().map(u32::from).filter(|p| *p != 0);
        let msg = self
            .core
            .message_from(node.data(), node.sub_node(), &self.core)?;
        Ok((Box::new(msg), parent))
    }
}

struct MessageImpl {
    core: Rc<Core>,
    pc: PcObject,
}

impl MessageImpl {
    /// The message's sub-node of NID type `nid_type`, preferring the
    /// specification's fixed id (0x692 recipients, 0x671 attachments).
    fn table_node(&self, fixed: u32, nid_type: u32) -> Option<(u32, BlockIdT, Option<BlockIdT>)> {
        if let Some((d, s)) = self.pc.subnodes.get(&fixed) {
            return Some((fixed, *d, *s));
        }
        self.pc
            .subnodes
            .iter()
            .find(|(nid, _)| *nid & NID_TYPE_MASK == nid_type)
            .map(|(nid, (d, s))| (*nid, *d, *s))
    }
}

impl MessageObject for MessageImpl {
    fn props(&self) -> &PropMap {
        &self.pc.props
    }

    fn read_prop(&self, prop: u16, cap: usize) -> ItemResult<Option<RawProp>> {
        self.core.read_value(&self.pc, prop, cap)
    }

    fn recipients(&self, max: usize) -> ItemResult<(Vec<PropMap>, usize)> {
        let Some((nid, data, sub)) = self.table_node(0x692, NID_TYPE_RECIPIENT_TABLE) else {
            return Ok((Vec::new(), 0));
        };
        let tc = self.core.table(nid, data, sub)?;
        self.core.table_rows(&tc, max)
    }

    fn attachments(&self) -> ItemResult<Vec<AttachmentRow>> {
        let Some((nid, data, sub)) = self.table_node(0x671, NID_TYPE_ATTACHMENT_TABLE) else {
            return Ok(Vec::new());
        };
        let tc = self.core.table(nid, data, sub)?;
        Ok(self
            .core
            .table_row_ids(&tc)
            .into_iter()
            .map(|nid| AttachmentRow { nid })
            .collect())
    }

    fn attachment(&self, row: AttachmentRow) -> ItemResult<Box<dyn AttachmentObject>> {
        if row.nid & NID_TYPE_MASK != NID_TYPE_ATTACHMENT {
            return Err(ItemError::corrupt("attachment row id is not an attachment"));
        }
        let (data, sub) = self
            .pc
            .subnodes
            .get(&row.nid)
            .copied()
            .ok_or_else(|| ItemError::corrupt("attachment sub-node missing"))?;
        let pc = self.core.pc(data, sub)?;
        Ok(Box::new(AttachmentImpl {
            core: self.core.clone(),
            pc,
            parent_subnodes: self.pc.subnodes.clone(),
        }))
    }
}

struct AttachmentImpl {
    core: Rc<Core>,
    pc: PcObject,
    parent_subnodes: Rc<SubNodes>,
}

impl AttachmentObject for AttachmentImpl {
    fn props(&self) -> &PropMap {
        &self.pc.props
    }

    fn data_size(&self) -> ItemResult<Option<u64>> {
        let Some(rec) = self.pc.records.get(&id::ATTACH_DATA) else {
            return Ok(None);
        };
        if rec.ptype != PT_BINARY {
            return Ok(None);
        }
        if rec.hnid == 0 {
            return Ok(Some(0));
        }
        if rec.hnid & NID_TYPE_MASK == 0 {
            return Ok(Some(self.pc.heap.get(rec.hnid)?.len() as u64));
        }
        let (data, _) = self
            .pc
            .subnodes
            .get(&rec.hnid)
            .copied()
            .ok_or_else(|| ItemError::corrupt("attachment data sub-node missing"))?;
        Ok(Some(self.core.tree_size(data)?))
    }

    fn write_data(&self, w: &mut dyn Write, max: u64) -> ItemResult<u64> {
        let rec = self
            .pc
            .records
            .get(&id::ATTACH_DATA)
            .ok_or_else(|| ItemError::corrupt("attachment data missing"))?;
        if rec.ptype != PT_BINARY {
            return Err(ItemError::corrupt("attachment data is not binary"));
        }
        let write_err = |e: io::Error| ItemError::io(&e, "cannot write attachment file");
        if rec.hnid == 0 {
            return Ok(0);
        }
        if rec.hnid & NID_TYPE_MASK == 0 {
            let bytes = self.pc.heap.get(rec.hnid)?;
            if bytes.len() as u64 > max {
                return Err(ItemError::new(Code::TooLarge, "attachment exceeds limit"));
            }
            w.write_all(bytes).map_err(write_err)?;
            return Ok(bytes.len() as u64);
        }
        let (data, _) = self
            .pc
            .subnodes
            .get(&rec.hnid)
            .copied()
            .ok_or_else(|| ItemError::corrupt("attachment data sub-node missing"))?;
        if self.core.tree_size(data)? > max {
            return Err(ItemError::new(Code::TooLarge, "attachment exceeds limit"));
        }
        // Streamed one data block (<= 8 KiB) at a time; the size is enforced
        // again while writing in case the tree header understates it.
        let mut written = 0u64;
        self.core.for_each_leaf(data, |chunk| {
            written += chunk.len() as u64;
            if written > max {
                return Err(ItemError::new(Code::TooLarge, "attachment exceeds limit"));
            }
            w.write_all(chunk).map_err(write_err)?;
            Ok(true)
        })?;
        Ok(written)
    }

    fn embedded(&self) -> ItemResult<Box<dyn MessageObject>> {
        let rec = self
            .pc
            .records
            .get(&id::ATTACH_DATA)
            .ok_or_else(|| ItemError::corrupt("embedded message object missing"))?;
        if rec.ptype != PT_OBJECT {
            return Err(ItemError::corrupt("embedded message object has wrong type"));
        }
        let nid = match self.pc.props.get(&id::ATTACH_DATA) {
            Some(PropValue::Object { nid, .. }) => *nid,
            _ => return Err(ItemError::corrupt("embedded message object unreadable")),
        };
        let (data, sub) = self
            .pc
            .subnodes
            .get(&nid)
            .or_else(|| self.parent_subnodes.get(&nid))
            .copied()
            .ok_or_else(|| ItemError::corrupt("embedded message sub-node missing"))?;
        let msg = self.core.message_from(data, sub, &self.core)?;
        Ok(Box::new(msg))
    }
}
