//! Bounded parsers for the LTP layer structures the engine reads itself:
//! Heap-on-Node (MS-PST 2.3.1), BTree-on-Heap (2.3.2) and Property Context
//! (2.3.3). They operate on already-decoded data blocks, are identical for
//! ANSI and Unicode files, and never trust a count or offset without a check.
//!
//! The `outlook-pst` crate has its own implementations, but they are only
//! reachable through crate-private traits and they materialise every property
//! (including multi-hundred-megabyte attachment data) eagerly. Owning this
//! small layer lets the engine decide, property by property, what to read and
//! how much.

use crate::error::{ItemError, ItemResult};
use crate::props::{decode_value, PropValue, PT_BOOLEAN, PT_ERROR, PT_F32, PT_I16, PT_I32};

const HN_SIGNATURE: u8 = 0xEC;
pub const CLIENT_SIG_BTH: u8 = 0xB5;
pub const CLIENT_SIG_PC: u8 = 0xBC;

/// Upper bounds that keep hostile heaps cheap to reject.
const MAX_BTH_LEVELS: u8 = 8;
const MAX_BTH_NODES: usize = 65_536;
const MAX_BTH_RECORDS: usize = 1 << 20;

/// A Heap-on-Node: the decoded data blocks of one node's data tree.
pub struct Heap {
    blocks: Vec<Vec<u8>>,
    client_sig: u8,
    user_root: u32,
}

fn rd16(b: &[u8], off: usize) -> ItemResult<u16> {
    b.get(off..off + 2)
        .map(|s| u16::from_le_bytes([s[0], s[1]]))
        .ok_or_else(|| ItemError::corrupt("heap: read past end of block"))
}

fn rd32(b: &[u8], off: usize) -> ItemResult<u32> {
    b.get(off..off + 4)
        .map(|s| u32::from_le_bytes([s[0], s[1], s[2], s[3]]))
        .ok_or_else(|| ItemError::corrupt("heap: read past end of block"))
}

impl Heap {
    pub fn new(blocks: Vec<Vec<u8>>) -> ItemResult<Self> {
        let first = blocks
            .first()
            .ok_or_else(|| ItemError::corrupt("heap: empty data tree"))?;
        if first.len() < 12 {
            return Err(ItemError::corrupt("heap: block too small"));
        }
        if first[2] != HN_SIGNATURE {
            return Err(ItemError::corrupt("heap: bad signature"));
        }
        let client_sig = first[3];
        let user_root = rd32(first, 4)?;
        Ok(Heap {
            blocks,
            client_sig,
            user_root,
        })
    }

    pub fn client_sig(&self) -> u8 {
        self.client_sig
    }

    pub fn user_root(&self) -> u32 {
        self.user_root
    }

    /// Bytes of heap allocation `hid`.
    pub fn get(&self, hid: u32) -> ItemResult<&[u8]> {
        if hid & 0x1F != 0 {
            return Err(ItemError::corrupt("heap: id is not a HID"));
        }
        let index = ((hid >> 5) & 0x7FF) as usize;
        let block_index = (hid >> 16) as usize;
        if index == 0 {
            return Err(ItemError::corrupt("heap: zero allocation index"));
        }
        let block = self
            .blocks
            .get(block_index)
            .ok_or_else(|| ItemError::corrupt("heap: block index out of range"))?;
        let ib_hnpm = rd16(block, 0)? as usize;
        let c_alloc = rd16(block, ib_hnpm)? as usize;
        if index > c_alloc {
            return Err(ItemError::corrupt("heap: allocation index out of range"));
        }
        // rgibAlloc starts after cAlloc and cFree.
        let base = ib_hnpm + 4;
        let start = rd16(block, base + (index - 1) * 2)? as usize;
        let end = rd16(block, base + index * 2)? as usize;
        if start > end || end > ib_hnpm || end > block.len() {
            return Err(ItemError::corrupt("heap: allocation bounds invalid"));
        }
        Ok(&block[start..end])
    }
}

/// One BTH record: key bytes and data bytes.
pub struct BthRecord {
    pub key: Vec<u8>,
    pub data: Vec<u8>,
}

/// Read every record of the BTH whose header lives at `header_hid`.
pub fn read_bth(heap: &Heap, header_hid: u32) -> ItemResult<Vec<BthRecord>> {
    let hdr = heap.get(header_hid)?;
    if hdr.len() < 8 || hdr[0] != CLIENT_SIG_BTH {
        return Err(ItemError::corrupt("bth: bad header"));
    }
    let cb_key = hdr[1] as usize;
    let cb_ent = hdr[2] as usize;
    let levels = hdr[3];
    let root = rd32(hdr, 4)?;
    if !(1..=16).contains(&cb_key) || cb_ent > 32 || levels > MAX_BTH_LEVELS {
        return Err(ItemError::corrupt("bth: unsupported geometry"));
    }
    let mut out = Vec::new();
    if root == 0 {
        return Ok(out);
    }
    // Explicit work list: (hid, level). Levels strictly decrease, so the walk
    // terminates; node and record counts are capped against DAG blow-ups.
    let mut work = vec![(root, levels)];
    let mut nodes = 0usize;
    while let Some((hid, level)) = work.pop() {
        nodes += 1;
        if nodes > MAX_BTH_NODES {
            return Err(ItemError::corrupt("bth: too many index nodes"));
        }
        let data = heap.get(hid)?;
        if level == 0 {
            let rec = cb_key + cb_ent;
            if rec == 0 || data.len() % rec != 0 {
                return Err(ItemError::corrupt("bth: leaf size mismatch"));
            }
            for chunk in data.chunks_exact(rec) {
                out.push(BthRecord {
                    key: chunk[..cb_key].to_vec(),
                    data: chunk[cb_key..].to_vec(),
                });
                if out.len() > MAX_BTH_RECORDS {
                    return Err(ItemError::corrupt("bth: too many records"));
                }
            }
        } else {
            let rec = cb_key + 4;
            if data.len() % rec != 0 {
                return Err(ItemError::corrupt("bth: index size mismatch"));
            }
            // Push in reverse so records come out in key order.
            for chunk in data.chunks_exact(rec).rev() {
                let child = rd32(chunk, cb_key)?;
                work.push((child, level - 1));
            }
        }
    }
    Ok(out)
}

/// One Property Context record (PCBTH).
#[derive(Clone, Copy, Debug)]
pub struct PcRecord {
    pub prop_id: u16,
    pub ptype: u16,
    pub hnid: u32,
}

/// Read the property records of a Property Context heap.
pub fn read_pc(heap: &Heap) -> ItemResult<Vec<PcRecord>> {
    if heap.client_sig() != CLIENT_SIG_PC {
        return Err(ItemError::corrupt("pc: heap is not a property context"));
    }
    let records = read_bth(heap, heap.user_root())?;
    records
        .into_iter()
        .map(|r| {
            if r.key.len() != 2 || r.data.len() != 6 {
                return Err(ItemError::corrupt("pc: bad record size"));
            }
            Ok(PcRecord {
                prop_id: u16::from_le_bytes([r.key[0], r.key[1]]),
                ptype: u16::from_le_bytes([r.data[0], r.data[1]]),
                hnid: u32::from_le_bytes([r.data[2], r.data[3], r.data[4], r.data[5]]),
            })
        })
        .collect()
}

/// Decode a PC record: inline values and heap values are read now, sub-node
/// values become `Deferred`.
pub fn pc_value(heap: &Heap, rec: &PcRecord) -> ItemResult<PropValue> {
    match rec.ptype {
        PT_I16 => Ok(PropValue::Int((rec.hnid & 0xFFFF) as u16 as i16 as i64)),
        PT_I32 | PT_ERROR => Ok(PropValue::Int(rec.hnid as i32 as i64)),
        PT_BOOLEAN => Ok(PropValue::Bool(rec.hnid & 0xFF != 0)),
        PT_F32 => Ok(PropValue::Other(PT_F32)),
        ptype => {
            if rec.hnid == 0 {
                // An empty HNID is an empty value for variable-size types.
                return Ok(decode_value(ptype, &[]));
            }
            if rec.hnid & 0x1F == 0 {
                Ok(decode_value(ptype, heap.get(rec.hnid)?))
            } else {
                Ok(PropValue::Deferred {
                    ptype,
                    nid: rec.hnid,
                })
            }
        }
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use crate::props::{PT_BINARY, PT_UNICODE};

    /// Build a one-block heap from allocations (allocation 1 is the user root).
    pub fn build_heap(client_sig: u8, allocs: &[Vec<u8>]) -> Vec<u8> {
        let mut block = vec![0u8; 12];
        block[2] = HN_SIGNATURE;
        block[3] = client_sig;
        block[4..8].copy_from_slice(&(1u32 << 5).to_le_bytes());
        let mut offsets = vec![12u16];
        for a in allocs {
            block.extend_from_slice(a);
            offsets.push(block.len() as u16);
        }
        if block.len() % 2 == 1 {
            block.push(0);
        }
        let ib_hnpm = block.len() as u16;
        block[0..2].copy_from_slice(&ib_hnpm.to_le_bytes());
        block.extend_from_slice(&(allocs.len() as u16).to_le_bytes());
        block.extend_from_slice(&0u16.to_le_bytes());
        for o in offsets {
            block.extend_from_slice(&o.to_le_bytes());
        }
        block
    }

    pub fn hid(index: u32) -> u32 {
        index << 5
    }

    /// A PC heap: alloc 1 = BTH header, alloc 2 = leaf records, then values.
    pub fn build_pc(props: &[(u16, u16, Vec<u8>)]) -> Vec<u8> {
        let mut allocs = vec![Vec::new(), Vec::new()];
        let mut leaf = Vec::new();
        for (pid, ptype, value) in props {
            let hnid = match *ptype {
                PT_I32 | PT_BOOLEAN | PT_I16 => {
                    let mut v = [0u8; 4];
                    v[..value.len().min(4)].copy_from_slice(&value[..value.len().min(4)]);
                    u32::from_le_bytes(v)
                }
                _ => {
                    allocs.push(value.clone());
                    hid(allocs.len() as u32)
                }
            };
            leaf.extend_from_slice(&pid.to_le_bytes());
            leaf.extend_from_slice(&ptype.to_le_bytes());
            leaf.extend_from_slice(&hnid.to_le_bytes());
        }
        let mut header = vec![CLIENT_SIG_BTH, 2, 6, 0];
        header.extend_from_slice(&hid(2).to_le_bytes());
        allocs[0] = header;
        allocs[1] = leaf;
        build_heap(CLIENT_SIG_PC, &allocs)
    }

    #[test]
    fn parses_property_context() {
        let subject: Vec<u8> = "Ciao àèì"
            .encode_utf16()
            .flat_map(|u| u.to_le_bytes())
            .collect();
        let block = build_pc(&[
            (0x0037, PT_UNICODE, subject),
            (0x0E07, PT_I32, 17i32.to_le_bytes().to_vec()),
            (0x3701, PT_BINARY, vec![1, 2, 3]),
        ]);
        let heap = Heap::new(vec![block]).unwrap();
        let recs = read_pc(&heap).unwrap();
        assert_eq!(recs.len(), 3);
        let vals: Vec<_> = recs.iter().map(|r| pc_value(&heap, r).unwrap()).collect();
        assert_eq!(vals[0], PropValue::Unicode("Ciao àèì".into()));
        assert_eq!(vals[1], PropValue::Int(17));
        assert_eq!(vals[2], PropValue::Binary(vec![1, 2, 3]));
    }

    #[test]
    fn rejects_garbage_without_panicking() {
        for len in 0..64usize {
            let block: Vec<u8> = (0..len).map(|i| (i * 37 % 251) as u8).collect();
            if let Ok(heap) = Heap::new(vec![block]) {
                let _ = read_pc(&heap);
            }
        }
        // Out-of-range allocation index.
        let block = build_heap(
            CLIENT_SIG_PC,
            &[vec![CLIENT_SIG_BTH, 2, 6, 0, 0xE0, 0xFF, 0, 0]],
        );
        let heap = Heap::new(vec![block]).unwrap();
        assert!(read_pc(&heap).is_err());
    }

    #[test]
    fn subnode_values_are_deferred() {
        let heap = Heap::new(vec![build_pc(&[])]).unwrap();
        let rec = PcRecord {
            prop_id: 0x1000,
            ptype: PT_UNICODE,
            hnid: 0x0000_8001 | 0x1F,
        };
        assert!(matches!(
            pc_value(&heap, &rec).unwrap(),
            PropValue::Deferred { .. }
        ));
    }
}
