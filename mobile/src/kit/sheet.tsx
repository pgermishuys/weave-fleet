import { useEffect, useRef, useState, type ReactNode } from "react";
import { Animated, Modal, PanResponder, Platform, Pressable, ScrollView, useWindowDimensions, View } from "react-native";
import { useTheme } from "~/theme";
import { Icon } from "./icons";
import { Text } from "./text";

/** A pull-up sheet: dims what is behind, grabber on top, drag the header down (or tap outside) to close. */
export function Sheet({ open, onClose, title, children, testID }: {
  open: boolean; onClose: () => void; title?: string; children: ReactNode; testID?: string;
}) {
  const { color, radius, scheme } = useTheme();
  const { height } = useWindowDimensions();
  const sheetHeight = Math.round(height * 0.82);
  const y = useRef(new Animated.Value(sheetHeight)).current;
  const [mounted, setMounted] = useState(open);
  const native = Platform.OS !== "web";

  useEffect(() => {
    if (open) {
      setMounted(true);
      y.setValue(sheetHeight);
      Animated.timing(y, { toValue: 0, duration: 240, useNativeDriver: native }).start();
    } else if (mounted) {
      Animated.timing(y, { toValue: sheetHeight, duration: 200, useNativeDriver: native }).start(() => setMounted(false));
    }
  }, [open]); // eslint-disable-line react-hooks/exhaustive-deps

  const drag = useRef(PanResponder.create({
    onMoveShouldSetPanResponder: (_, g) => g.dy > 4 && Math.abs(g.dy) > Math.abs(g.dx),
    onPanResponderMove: (_, g) => y.setValue(Math.max(0, g.dy)),
    onPanResponderRelease: (_, g) => {
      if (g.dy > 120 || g.vy > 0.8) onCloseRef.current();
      else Animated.spring(y, { toValue: 0, useNativeDriver: native }).start();
    },
  })).current;
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  if (!mounted) return null;
  return (
    <Modal transparent visible animationType="none" onRequestClose={onClose} statusBarTranslucent>
      <View style={{ flex: 1, justifyContent: "flex-end" }} testID={testID}>
        <Pressable
          accessibilityLabel="Close" onPress={onClose}
          style={{ position: "absolute", top: 0, left: 0, right: 0, bottom: 0, backgroundColor: scheme === "light" ? "rgba(26,25,24,0.28)" : "rgba(0,0,0,0.38)" }}
        />
        <Animated.View style={{
          height: sheetHeight, transform: [{ translateY: y }], backgroundColor: color.card, borderTopLeftRadius: radius.sheet,
          borderTopRightRadius: radius.sheet, borderTopWidth: 1, borderColor: color.border,
        }}>
          <View {...drag.panHandlers} style={{ paddingTop: 8 }}>
            <View style={{ width: 36, height: 4, borderRadius: 2, backgroundColor: color.tint9, alignSelf: "center", marginBottom: 6 }} />
            <View style={{ flexDirection: "row", alignItems: "center", minHeight: 44, paddingLeft: 16, paddingRight: 4 }}>
              <Text weight="semibold" size={16} style={{ flex: 1 }} numberOfLines={1}>{title}</Text>
              <Pressable testID="sheet-close" onPress={onClose} accessibilityRole="button" accessibilityLabel="Close sheet" style={{ width: 44, height: 44, alignItems: "center", justifyContent: "center" }}>
                <Icon name="x" size={18} color={color.muted} />
              </Pressable>
            </View>
          </View>
          <ScrollView contentContainerStyle={{ paddingHorizontal: 16, paddingBottom: 32 }}>{children}</ScrollView>
        </Animated.View>
      </View>
    </Modal>
  );
}
