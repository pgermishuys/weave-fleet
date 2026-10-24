import type { ReactNode } from "react";
import { ActivityIndicator, Pressable, View, type StyleProp, type ViewStyle } from "react-native";
import { useTheme } from "~/theme";
import { Icon, type IconName } from "./icons";
import { alpha, Text } from "./text";

export function Button({ label, onPress, variant = "secondary", size = "md", icon, disabled, loading, testID, style }: {
  label: string; onPress: () => void; variant?: "primary" | "secondary" | "ghost" | "danger"; size?: "md" | "sm";
  icon?: IconName; disabled?: boolean; loading?: boolean; testID?: string; style?: StyleProp<ViewStyle>;
}) {
  const { color, radius } = useTheme();
  const solid = variant === "primary" || variant === "danger";
  const fg = solid ? color.onAccent : color.text;
  const bg = variant === "primary" ? color.accent : variant === "danger" ? color.error : variant === "secondary" ? color.panel : "transparent";
  const small = size === "sm";
  return (
    <Pressable
      onPress={onPress} disabled={disabled || loading} testID={testID} accessibilityRole="button" accessibilityLabel={label}
      accessibilityState={{ disabled: !!disabled, busy: !!loading }}
      hitSlop={small ? { top: 5, bottom: 5, left: 4, right: 4 } : undefined}
      style={({ pressed }) => [{
        height: small ? 34 : 44, paddingHorizontal: small ? 12 : 16, borderRadius: radius.button, backgroundColor: bg,
        borderWidth: 1, borderColor: variant === "secondary" ? color.border : "transparent",
        flexDirection: "row", alignItems: "center", justifyContent: "center", gap: 7,
        opacity: disabled ? 0.45 : pressed ? 0.8 : 1,
      }, style]}
    >
      {loading ? <ActivityIndicator size="small" color={fg} /> : icon ? <Icon name={icon} size={small ? 14 : 16} color={fg} /> : null}
      <Text weight="medium" size={small ? 13 : 15} style={{ color: fg }} numberOfLines={1}>{label}</Text>
    </Pressable>
  );
}

/** The rounded panel that sits on the window chrome: a hairline in dark, a soft lift in light. */
export function Panel({ children, style }: { children: ReactNode; style?: StyleProp<ViewStyle> }) {
  const { color, radius, scheme } = useTheme();
  return (
    <View style={[{
      backgroundColor: color.panel, borderRadius: radius.panel, borderWidth: 1, borderColor: color.border, overflow: "hidden",
    }, scheme === "light" && { borderColor: "transparent", shadowColor: "#1a1918", shadowOpacity: 0.1, shadowRadius: 14, shadowOffset: { width: 0, height: 6 }, elevation: 2 }, style]}>
      {children}
    </View>
  );
}

export function Card({ children, tone = "default", style, testID }: {
  children: ReactNode; tone?: "default" | "waiting"; style?: StyleProp<ViewStyle>; testID?: string;
}) {
  const { color, radius } = useTheme();
  const waiting = tone === "waiting";
  return (
    <View testID={testID} style={[{
      backgroundColor: waiting ? color.waitingBg : color.card, borderColor: waiting ? color.waitingEdge : color.border,
      borderWidth: 1, borderRadius: radius.card, padding: 12, gap: 10,
    }, style]}>
      {children}
    </View>
  );
}

type PillTone = "running" | "error" | "waiting" | "accent" | "muted";

function toneColors(tone: PillTone | "default", c: ReturnType<typeof useTheme>["color"]) {
  if (tone === "default" || tone === "muted") return { fg: c.muted, bg: tone === "muted" ? c.tint5 : "transparent" };
  if (tone === "accent") return { fg: c.accent, bg: c.accentDim };
  return { fg: c[tone], bg: alpha(c[tone], 0.1) };
}

/** A bordered label: PR, branch, machine. Tappable when `onPress` is given. */
export function Chip({ label, icon, tone = "default", mono, onPress }: {
  label: string; icon?: IconName; tone?: PillTone | "default"; mono?: boolean; onPress?: () => void;
}) {
  const { color } = useTheme();
  const { fg, bg } = toneColors(tone, color);
  const bordered = tone === "default";
  return (
    <Pressable
      onPress={onPress} disabled={!onPress} accessibilityRole={onPress ? "button" : undefined} accessibilityLabel={label}
      hitSlop={onPress ? 10 : undefined}
      style={{
        height: 24, paddingHorizontal: 8, borderRadius: 6, backgroundColor: bg, borderWidth: 1,
        borderColor: bordered ? color.border : "transparent", flexDirection: "row", alignItems: "center", gap: 5, alignSelf: "flex-start",
      }}
    >
      {icon ? <Icon name={icon} size={12} color={fg} /> : null}
      <Text size={12} weight="medium" mono={mono} style={{ color: fg }} numberOfLines={1}>{label}</Text>
    </Pressable>
  );
}

/** A small tinted result label: "41 passed". */
export function Pill({ label, tone }: { label: string; tone: PillTone }) {
  const { color } = useTheme();
  const { fg, bg } = toneColors(tone, color);
  const edge = tone === "muted" ? color.border : alpha(color[tone], 0.35);
  return (
    <View style={{ height: 20, paddingHorizontal: 7, borderRadius: 10, backgroundColor: tone === "muted" ? bg : alpha(color[tone], 0.12), borderWidth: 1, borderColor: edge, justifyContent: "center" }}>
      <Text size={11} weight="medium" style={{ color: fg }} numberOfLines={1}>{label}</Text>
    </View>
  );
}

export function SectionHeader({ label, count }: { label: string; count?: number }) {
  return (
    <View style={{ paddingHorizontal: 16, paddingTop: 14, paddingBottom: 4 }} accessibilityRole="header">
      <Text size={11.5} weight="semibold" tone="muted" style={{ letterSpacing: 0.6, textTransform: "uppercase" }}>
        {count === undefined ? label : `${label} · ${count}`}
      </Text>
    </View>
  );
}

export function EmptyState({ title, body, action }: { title: string; body?: string; action?: { label: string; onPress: () => void } }) {
  return (
    <View style={{ alignItems: "center", gap: 8, paddingHorizontal: 32, paddingVertical: 48 }}>
      <Text size={17} weight="semibold" style={{ textAlign: "center" }}>{title}</Text>
      {body ? <Text size={14} tone="muted" style={{ textAlign: "center" }}>{body}</Text> : null}
      {action ? <Button label={action.label} onPress={action.onPress} style={{ marginTop: 8 }} /> : null}
    </View>
  );
}
