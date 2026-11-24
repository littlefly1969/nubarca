import { useEffect, useState } from 'react';
import { Text, View } from 'react-native';
import type { PhotoTextLayout } from '../../../packages/contracts/src/photoText';

/** The same normalized frame as the web card, with measured, complete text. */
export function PhotoTextOverlay({ layout, text, author, width, height }: {
  layout: PhotoTextLayout; text: string; author: string; width: number; height: number;
}) {
  const w = width * layout.width; const h = height * layout.height;
  const bubble = layout.mode === 'bubble';
  const [size, setSize] = useState(w * .075);
  const room = h * (bubble ? .68 : .84);
  useEffect(() => setSize(w * .075), [w, h, text, author, bubble]);
  return <View style={{ position: 'absolute', left: (width - w) * layout.x, top: (height - h) * layout.y,
    width: w, height: h }} testID="guestbook-photo-text">
    {bubble && <>
      <View style={{ position: 'absolute', left: w * .01, top: h * .01, width: w * .98, height: h * .83,
        backgroundColor: 'white', borderWidth: 1.5, borderColor: '#171717' }} />
      <View style={{ position: 'absolute', top: h * .83,
        left: layout.tail === 'left' ? w * .16 : w * .68,
        width: 0, height: 0, borderLeftWidth: w * .08, borderRightWidth: w * .08,
        borderTopWidth: h * .15, borderLeftColor: 'transparent', borderRightColor: 'transparent', borderTopColor: 'white' }} />
    </>}
    <View style={{ position: 'absolute', left: w * .08, top: h * .08, width: w * .84, height: room,
      justifyContent: 'center' }}>
      <Text onLayout={(e) => {
        if (e.nativeEvent.layout.height > room + 1 && size > 1) setSize((n) => n * .9);
      }} style={{ color: bubble ? '#171717' : 'white', textAlign: 'center', fontSize: size, lineHeight: size * 1.2,
        ...(bubble ? {} : { textShadowColor: 'black', textShadowRadius: 3, textShadowOffset: { width: 0, height: 1 } }) }}>
        {text}{'\n'}<Text style={{ fontSize: size * .75 }}>— {author}</Text>
      </Text>
    </View>
  </View>;
}
