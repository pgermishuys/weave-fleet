// Fleet's small building blocks in React Native: text in Inter, the panel on chrome, buttons, status glyph.
import { useEffect, useRef, type ReactNode } from "react";
import { Animated, Pressable, StyleSheet, Text, View, type StyleProp, type TextProps, type TextStyle, type ViewStyle } from "react-native";
import { font, radius, usePalette, type Palette } from "~/fleet/theme";

export function T({ style, muted, mono, weight, size = 15, ...rest }: TextProps & { muted?: boolean; mono?: boolean; weight?: "medium" | "semibold"; size?: number }) {
  const p = usePalette();
  const family = mono ? font.mono : weight ? font[weight] : font.regular;
  return <Text {...rest} style={[{ color: muted ? p.muted : p.text, fontFamily: family, fontSize: size, lineHeight: Math.round(size * 1.45) }, style]} />;
}

export function Panel({ children, style }: { children: ReactNode; style?: StyleProp<ViewStyle> }) {
  const p = usePalette();
  return <View style={[{ flex: 1, backgroundColor: p.panel, borderRadius: radius.panel, borderWidth: StyleSheet.hairlineWidth, borderColor: p.border, overflow: "hidden" }, style]}>{children}</View>;
}

export function Button({ label, onPress, kind = "secondary", disabled, style }: { label: string; onPress: () => void; kind?: "primary" | "secondary" | "danger"; disabled?: boolean; style?: StyleProp<ViewStyle> }) {
  const p = usePalette();
  const bg = kind === "primary" ? p.accent : p.card;
  const color: TextStyle["color"] = kind === "primary" ? p.onAccent : kind === "danger" ? p.error : p.text;
  return (
    <Pressable
      accessibilityRole="button"
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [
        { minHeight: 44, borderRadius: radius.button, paddingHorizontal: 16, alignItems: "center", justifyContent: "center", backgroundColor: bg, borderWidth: kind === "primary" ? 0 : StyleSheet.hairlineWidth, borderColor: p.border, opacity: disabled ? 0.45 : pressed ? 0.75 : 1, transform: [{ scale: pressed ? 0.98 : 1 }] },
        style,
      ]}
    >
      <T weight="semibold" style={{ color }}>{label}</T>
    </Pressable>
  );
}

/** Fleet's status glyphs: four ticking dots while working, a filled dot otherwise. */
export function StatusGlyph({ tone, size = 10 }: { tone: "working" | "needs-you" | "finished" | "error" | "idle"; size?: number }) {
  const p = usePalette();
  const tick = useRef(new Animated.Value(0)).current;
  useEffect(() => {
    if (tone !== "working") return;
    const loop = Animated.loop(Animated.timing(tick, { toValue: 4, duration: 1200, useNativeDriver: true }));
    loop.start();
    return () => loop.stop();
  }, [tone, tick]);
  if (tone === "working") {
    const dot = size / 2 - 1;
    return (
      <View style={{ width: size, height: size, flexDirection: "row", flexWrap: "wrap", gap: 2 }}>
        {[0, 1, 3, 2].map((i) => (
          <Animated.View key={i} style={{ width: dot, height: dot, borderRadius: dot, backgroundColor: p.text, opacity: tick.interpolate({ inputRange: [0, i, i + 0.5, i + 1, 4], outputRange: [0.35, 0.35, 1, 0.35, 0.35], extrapolate: "clamp" }) }} />
        ))}
      </View>
    );
  }
  const color = tone === "needs-you" ? p.waiting : tone === "error" ? p.error : tone === "finished" ? p.running : p.muted;
  return <View style={{ width: size - 2, height: size - 2, borderRadius: size, backgroundColor: color, margin: 1 }} />;
}

export function toneOf(activityStatus: string | null | undefined): "working" | "needs-you" | "finished" | "error" | "idle" {
  switch (activityStatus) {
    case "busy": case "working": case "retry": case "delegating": case "active": return "working";
    case "waiting_input": return "needs-you";
    case "error": return "error";
    default: return "idle";
  }
}

export const styles = (p: Palette) => StyleSheet.create({
  chrome: { flex: 1, backgroundColor: p.bg },
  divider: { height: StyleSheet.hairlineWidth, backgroundColor: p.border },
});
