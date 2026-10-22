//! The vendored reader (vendor/outlook-pst/NUBARCA-PATCH.md) accepts a heap
//! page whose cFree does not count its zero-length allocations, as real PSTs
//! write them, and still rejects malformed allocation offsets.

use outlook_pst::ltp::heap::{HeapNodePageAllocOffsets, HeapNodePageMap};

#[test]
fn a_heap_page_with_an_uncounted_zero_length_allocation_is_readable() {
    // Three allocations, the middle one empty; the writer left cFree = 0.
    let offsets = HeapNodePageAllocOffsets::new(vec![12, 20, 20, 30]);
    let map = HeapNodePageMap::new(3, 0, offsets).expect("cFree is informational");
    let sizes: Vec<u16> = map.allocations().iter().map(|a| a.size()).collect();
    assert_eq!(sizes, vec![8, 0, 10]);
}

#[test]
fn malformed_allocation_offsets_are_still_rejected() {
    // Offsets going backwards.
    assert!(HeapNodePageMap::new(2, 0, HeapNodePageAllocOffsets::new(vec![12, 30, 20])).is_err());
    // A count that does not match the offsets.
    assert!(HeapNodePageMap::new(5, 0, HeapNodePageAllocOffsets::new(vec![12, 20, 30])).is_err());
}
