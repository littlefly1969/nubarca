// PHASE B SPIKE — NOT PRODUCTION. Deleted before this slice's final commit.
//
// It answers one question in isolation: can a single ID-based anchor make a
// dynamic `numColumns` change keep the user's place, using nothing but
// FlashList's own API?
//
// The 2.0.2 baseline this is built on, measured here on a real emulator:
// append works, recycling works, no blank cells, no crash, `numColumns`
// changes without a remount, 5 -> 3 keeps context — but 3 -> 5 resets to
// item 0 from any depth. That last case is the whole reason this file exists.
//
// No images and no network: a blank cell or a lost position in this screen is
// the list engine's, not the image loader's. Every cell shows its own index,
// which is what makes the position observable in a screenshot.
import React, { useCallback, useLayoutEffect, useRef, useState } from 'react';
import { StyleSheet, Text, View, useWindowDimensions } from 'react-native';
import { FlashList, type FlashListRef, type ViewToken } from '@shopify/flash-list';
import { Screen, AppHeader, Button } from '../src/ui/components';
import { columnsForWidth, grid, spacing, typography } from '../src/ui/tokens';
import { themed } from '../src/ui/theme';

interface SpikeItem {
  id: string;
  index: number;
}

const page = (from: number, count: number): SpikeItem[] =>
  Array.from({ length: count }, (_, i) => ({ id: `spike-${from + i}`, index: from + i }));

// A tile counts as the user's context once it is meaningfully on screen, not
// when one row of pixels peeks in at the top edge.
const VIEWABILITY = { itemVisiblePercentThreshold: 60 };

export default function SpikeFlashList(): React.JSX.Element {
  const styles = useStyles();
  const { width } = useWindowDimensions();
  const columns = columnsForWidth(width);
  const [items, setItems] = useState<SpikeItem[]>(() => page(0, 60));

  const listRef = useRef<FlashListRef<SpikeItem>>(null);
  // The canonical anchor: what the user is looking at, as a stable ID.
  const visibleItemIdRef = useRef<string | null>(null);
  const previousColumnsRef = useRef(columns);
  // Non-null only between detecting a column change and finishing its restore.
  const pendingColumnAnchorRef = useRef<string | null>(null);

  const append = useCallback(() => {
    setItems((current) =>
      current.length >= 180 ? current : [...current, ...page(current.length, 60)],
    );
  }, []);

  const keyExtractor = useCallback((item: SpikeItem) => item.id, []);

  const onViewableItemsChanged = useCallback(
    ({ viewableItems }: { viewableItems: ViewToken<SpikeItem>[] }) => {
      // Mid-restore the visible window still describes the position we are
      // correcting, so letting it write back would anchor us to the very reset
      // we are undoing.
      if (pendingColumnAnchorRef.current !== null) return;
      const first = viewableItems[0]?.item;
      if (first) visibleItemIdRef.current = first.id;
    },
    [],
  );

  // A column change is the ONLY trigger. This effect runs at React commit,
  // which is BEFORE FlashList has relaid out for the new column count, so it
  // only ARMS the restore: the anchor read here is still the one the user had,
  // and scrolling now would be undone by the relayout that follows.
  useLayoutEffect(() => {
    if (previousColumnsRef.current === columns) return;
    previousColumnsRef.current = columns;
    pendingColumnAnchorRef.current = visibleItemIdRef.current;
  }, [columns]);

  // ...and this fires once FlashList has committed that layout, which is the
  // first moment an index means what we think it means. Refs only in here: the
  // documented hazard of this hook is setState.
  const onCommitLayoutEffect = useCallback(() => {
    const id = pendingColumnAnchorRef.current;
    if (id === null) return;
    const listWidth = listRef.current?.getWindowSize().width;
    if (listWidth === undefined) return;
    // A rotation produces SEVERAL layout commits, and the early ones carry the
    // new column count inside the OLD viewport: 5 columns laid out in 409dp
    // gives 82dp rows, so an index resolved there points at an offset that is
    // about to be wrong by a factor of two. Asking the app's own function what
    // the list's measured width implies is an exact test for "has the list
    // caught up", with no tolerance to tune and no geometry to cache.
    if (columnsForWidth(listWidth) !== columns) return;
    // Consumed once: a still-armed anchor would start a second scroll that
    // fights the first, since each pauses offset correction and runs its own
    // convergence steps.
    pendingColumnAnchorRef.current = null;
    const index = items.findIndex((item) => item.id === id);
    if (index < 0) return;
    // Two calls, not a retry loop, and centred rather than pinned to the top.
    // The first call cannot be exact: a deep index sits far outside the
    // rendered window, so FlashList only has an ESTIMATE of its offset from
    // average item size, and that error grows with distance — measured here as
    // invisible at index 33 and two full rows at index 105. The first scroll
    // renders and measures the target region; the second is then arithmetic on
    // real layout, and a third has nothing left to improve. Centring spends
    // whatever remains on empty space instead of on pushing the user's own
    // tile off screen.
    const list = listRef.current;
    if (list === null) return;
    void list
      .scrollToIndex({ index, animated: false, viewPosition: 0.5 })
      .then(() => list.scrollToIndex({ index, animated: false, viewPosition: 0.5 }))
      .catch(() => undefined)
      .finally(() => {
      });
  }, [items, columns]);

  const renderItem = useCallback(
    ({ item }: { item: SpikeItem }) => (
      <View style={styles.cell}>
        <View style={styles.tile}>
          <Text style={styles.index}>{item.index}</Text>
        </View>
      </View>
    ),
    [styles],
  );

  return (
    <Screen>
      <AppHeader
        title={`spike ${items.length} · ${columns} col`}
        actions={<Button label="+60" onPress={append} variant="secondary" />}
      />
      <FlashList
        ref={listRef}
        data={items}
        keyExtractor={keyExtractor}
        numColumns={columns}
        renderItem={renderItem}
        onViewableItemsChanged={onViewableItemsChanged}
        onCommitLayoutEffect={onCommitLayoutEffect}
        viewabilityConfig={VIEWABILITY}
        contentContainerStyle={styles.content}
      />
    </Screen>
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    content: { paddingHorizontal: grid.gap / 2, paddingBottom: spacing.xxl },
    // Half a gutter per side gives a whole one between neighbours.
    cell: { flex: 1, padding: grid.gap / 2 },
    tile: {
      aspectRatio: 1,
      backgroundColor: colors.surfaceSubtle,
      borderWidth: StyleSheet.hairlineWidth,
      borderColor: colors.separator,
      alignItems: 'center',
      justifyContent: 'center',
    },
    index: { ...typography.sectionTitle, color: colors.textSecondary },
  }),
);
