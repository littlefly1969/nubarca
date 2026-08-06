import React from 'react';
import { Modal, Pressable, StyleSheet, Text, View } from 'react-native';
import { mediumPreviewPath } from '../api/gallery';
import AuthedImage from './AuthedImage';

// Full-screen modal viewer. Uses the MEDIUM preview derivative (never the
// original full-res), loaded through AuthedImage — which owns its own loading
// spinner and retryable-error state, so this component just frames it. Takes
// only the logical id + display name (works for both folder files and photos).
export default function ImageViewer({
  file,
  onClose,
}: {
  file: { id: string; name: string } | null;
  onClose: () => void;
}): React.JSX.Element {
  return (
    <Modal
      visible={file !== null}
      transparent={false}
      animationType="fade"
      onRequestClose={onClose}
    >
      <View style={styles.backdrop}>
        <Pressable style={styles.closeBtn} onPress={onClose} hitSlop={12}>
          <Text style={styles.closeText}>✕</Text>
        </Pressable>

        {file !== null && (
          <>
            <AuthedImage
              style={styles.image}
              resizeMode="contain"
              path={mediumPreviewPath(file.id)}
            />
            <Text style={styles.caption} numberOfLines={1}>
              {file.name}
            </Text>
          </>
        )}
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  backdrop: {
    flex: 1,
    backgroundColor: '#000',
    alignItems: 'center',
    justifyContent: 'center',
  },
  image: { ...StyleSheet.absoluteFillObject, width: '100%', height: '100%' },
  closeBtn: {
    position: 'absolute',
    top: 48,
    right: 20,
    zIndex: 2,
    width: 40,
    height: 40,
    borderRadius: 20,
    backgroundColor: 'rgba(0,0,0,0.5)',
    alignItems: 'center',
    justifyContent: 'center',
  },
  closeText: { color: '#fff', fontSize: 20, fontWeight: '600' },
  caption: {
    position: 'absolute',
    bottom: 40,
    left: 20,
    right: 20,
    color: '#fff',
    fontSize: 14,
    textAlign: 'center',
  },
});
