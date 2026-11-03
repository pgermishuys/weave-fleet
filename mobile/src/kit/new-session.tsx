// The parts of the New session screen: the line under the box that says where it runs, the reason it can't start,
// the chip for the harness and model, and the rows and search box of the sheets.
import { Pressable, ScrollView, TextInput, View } from "react-native";
import { useTheme } from "~/theme";
import { Icon, type IconName } from "./icons";
import { alpha, Text } from "./text";

export interface WherePart {
  key: string;
  label: string;
  /** Lit when this part is what to change (the machine is offline, no folder yet). */
  tone?: "warning" | "error";
}

/** `machine ⌄ · folder ⌄ · New worktree ⌄`: one row, sideways when it's too long, and each part opens a sheet. */
export function WhereLine({ parts, onPress }: { parts: WherePart[]; onPress: (key: string) => void }) {
  const { color } = useTheme();
  return (
    <ScrollView horizontal showsHorizontalScrollIndicator={false} keyboardShouldPersistTaps="handled" testID="where-line" contentContainerStyle={{ alignItems: "center", paddingHorizontal: 2 }}>
      {parts.map((part, index) => {
        const lit = part.tone ? (part.tone === "error" ? color.error : color.waiting) : null;
        return (
          <View key={part.key} style={{ flexDirection: "row", alignItems: "center" }}>
            {index > 0 ? <Text size={13} tone="muted" style={{ marginHorizontal: 2 }}>·</Text> : null}
            <Pressable
              testID={`where-${part.key}`} onPress={() => onPress(part.key)} accessibilityRole="button" accessibilityLabel={part.label}
              style={{ minHeight: 40, paddingHorizontal: 8, flexDirection: "row", alignItems: "center", gap: 3, justifyContent: "center" }}
            >
              <View style={{ flexDirection: "row", alignItems: "center", gap: 3, paddingHorizontal: lit ? 7 : 0, height: 26, borderRadius: 13, backgroundColor: lit ? alpha(lit, 0.14) : "transparent" }}>
                <Text size={12.5} weight={lit ? "medium" : "regular"} numberOfLines={1} style={{ color: lit ?? color.muted, maxWidth: 160 }}>{part.label}</Text>
                <Icon name="chevron-down" size={12} color={lit ?? color.muted} />
              </View>
            </Pressable>
          </View>
        );
      })}
    </ScrollView>
  );
}

/** Why it can't start, above the box and before anything is typed. */
export function Notice({ tone, lead, rest, testID }: { tone: "warning" | "error"; lead: string; rest: string; testID?: string }) {
  const { color, radius } = useTheme();
  const tint = tone === "error" ? color.error : color.waiting;
  return (
    <View testID={testID} accessibilityRole="alert" style={{ flexDirection: "row", gap: 9, padding: 10, borderRadius: radius.card, borderWidth: 1, borderColor: alpha(tint, 0.4), backgroundColor: alpha(tint, 0.1) }}>
      <View style={{ paddingTop: 1 }}><Icon name="alert" size={16} color={tint} /></View>
      <Text size={13} style={{ flex: 1 }}><Text size={13} weight="semibold">{lead} </Text>{rest}</Text>
    </View>
  );
}

/** The chip in the box's toolbar: which harness and model the session starts with. `tone` lights it when there's none. */
export function ModelChip({ label, onPress, tone }: { label: string; onPress: () => void; tone?: "warning" }) {
  const { color } = useTheme();
  const lit = tone === "warning" ? color.waiting : null;
  return (
    <Pressable
      testID="model-chip" onPress={onPress} accessibilityRole="button" accessibilityLabel={label} hitSlop={{ top: 6, bottom: 6 }}
      style={{ height: 30, maxWidth: "100%", flexShrink: 1, paddingHorizontal: 10, borderRadius: 15, borderWidth: 1, borderColor: lit ? alpha(lit, 0.5) : color.border, backgroundColor: lit ? alpha(lit, 0.12) : color.tint5, flexDirection: "row", alignItems: "center", gap: 4 }}
    >
      <Text size={12.5} numberOfLines={1} style={{ color: lit ?? color.text, flexShrink: 1 }}>{label}</Text>
      <Icon name="chevron-down" size={12} color={lit ?? color.muted} />
    </Pressable>
  );
}

/** A choice in a sheet: an icon, what it is, a line under it, and a check when it's the current one. */
export function OptionRow({ icon, title, detail, selected, onPress, testID, mono }: {
  icon?: IconName; title: string; detail?: string; selected?: boolean; onPress: () => void; testID?: string; mono?: boolean;
}) {
  const { color, radius } = useTheme();
  return (
    <Pressable
      testID={testID} onPress={onPress} accessibilityRole="button" accessibilityLabel={detail ? `${title}, ${detail}` : title} accessibilityState={{ selected: !!selected }}
      style={({ pressed }) => ({ minHeight: 52, flexDirection: "row", alignItems: "center", gap: 12, paddingHorizontal: 10, paddingVertical: 8, borderRadius: radius.card, backgroundColor: selected ? color.accentDim : pressed ? color.tint5 : "transparent" })}
    >
      {icon ? <Icon name={icon} size={18} color={color.muted} /> : null}
      <View style={{ flex: 1 }}>
        <Text size={15} weight="medium" numberOfLines={1}>{title}</Text>
        {detail ? <Text size={12} tone="muted" mono={mono} numberOfLines={1} ellipsizeMode="middle">{detail}</Text> : null}
      </View>
      {selected ? <Icon name="check" size={17} color={color.accent} /> : null}
    </Pressable>
  );
}

export function SearchField({ value, onChangeText, placeholder, testID }: { value: string; onChangeText: (text: string) => void; placeholder: string; testID?: string }) {
  const { color, font, radius } = useTheme();
  return (
    <View style={{ height: 44, flexDirection: "row", alignItems: "center", gap: 8, paddingHorizontal: 12, borderRadius: radius.card, borderWidth: 1, borderColor: color.border, backgroundColor: color.panel }}>
      <Icon name="search" size={16} color={color.muted} />
      <TextInput
        testID={testID} value={value} onChangeText={onChangeText} placeholder={placeholder} placeholderTextColor={color.muted} autoCapitalize="none" autoCorrect={false}
        style={{ flex: 1, color: color.text, fontFamily: font.regular, fontSize: 15, paddingVertical: 0 }}
      />
    </View>
  );
}
