import { Pressable, View } from "react-native";
import { useTheme } from "~/theme";
import { Button, Card } from "./controls";
import { StatusGlyph, type GlyphStatus } from "./glyph";
import { Icon } from "./icons";
import { Text } from "./text";

/** `newPages`: pages the agent showed since the phone last opened the session's Pages, as an accent chip. */
export function SessionRow({ title, detail, status, age, machine, newPages, onPress, onLongPress, testID }: {
  title: string; detail?: string; status: GlyphStatus; age?: string; machine?: string; newPages?: number;
  onPress: () => void; onLongPress?: () => void; testID?: string;
}) {
  const { color } = useTheme();
  const quiet = status === "idle" || status === "done";
  const pagesLabel = newPages === 1 ? "1 new page" : `${newPages} new pages`;
  return (
    <Pressable
      onPress={onPress} onLongPress={onLongPress} testID={testID} accessibilityRole="button" accessibilityLabel={newPages ? `${title}, ${pagesLabel}` : title}
      style={({ pressed }) => ({
        minHeight: 56, paddingHorizontal: 16, paddingVertical: 9, flexDirection: "row", gap: 11, alignItems: "flex-start",
        backgroundColor: pressed ? color.tint5 : "transparent",
      })}
    >
      <View style={{ width: 12, height: 21, alignItems: "center", justifyContent: "center" }}><StatusGlyph status={status} size={10} /></View>
      <View style={{ flex: 1, gap: 2 }}>
        <View style={{ flexDirection: "row", alignItems: "center", gap: 8 }}>
          <Text weight="medium" numberOfLines={1} style={{ flex: 1, color: quiet ? color.muted : color.rowText }}>{title}</Text>
          {age ? <Text size={12} tone="muted" style={{ fontVariant: ["tabular-nums"] }}>{age}</Text> : null}
        </View>
        {detail || machine || newPages ? (
          <View style={{ flexDirection: "row", alignItems: "center", gap: 7 }}>
            {newPages ? (
              <View testID="new-pages" style={{ height: 18, paddingHorizontal: 7, borderRadius: 9, backgroundColor: color.accentDim, flexDirection: "row", alignItems: "center", gap: 4 }}>
                <Icon name="page" size={11} color={color.accent} />
                <Text size={11} tone="accent">{pagesLabel}</Text>
              </View>
            ) : null}
            {detail ? <Text size={12.5} tone="muted" numberOfLines={1} style={{ flexShrink: 1 }}>{detail}</Text> : null}
            {machine ? (
              <View style={{ height: 18, paddingHorizontal: 7, borderRadius: 9, backgroundColor: color.tint5, justifyContent: "center" }}>
                <Text size={11} tone="muted">{machine}</Text>
              </View>
            ) : null}
          </View>
        ) : null}
      </View>
    </Pressable>
  );
}

export function PermissionCard({ title, session, machine, age, command, alwaysLabel, onAllow, onDeny, onAlways, busy, testID }: {
  title: string; session?: string; machine?: string; age?: string; command?: string; alwaysLabel?: string;
  onAllow: () => void; onDeny: () => void; onAlways?: () => void; busy?: boolean; testID?: string;
}) {
  const { color } = useTheme();
  return (
    <Card tone="waiting" testID={testID}>
      <View style={{ flexDirection: "row", alignItems: "center", gap: 8 }}>
        <Icon name="shield" size={17} color={color.waiting} />
        <Text weight="semibold" numberOfLines={1} style={{ flex: 1 }}>{title}</Text>
        {machine ? <Text size={11.5} tone="muted">{machine}</Text> : null}
        {age ? <Text size={12} tone="muted">{age}</Text> : null}
      </View>
      {session ? <Text size={14} weight="medium" numberOfLines={2}>{session}</Text> : null}
      {command ? (
        <View style={{ padding: 10, borderRadius: 7, backgroundColor: color.panel, borderWidth: 1, borderColor: color.border }}>
          <Text mono size={12.5} numberOfLines={4} selectable>{"$ " + command}</Text>
        </View>
      ) : null}
      <View style={{ flexDirection: "row", gap: 8 }}>
        <Button label="Allow once" variant="primary" onPress={onAllow} loading={busy} style={{ flex: 1 }} testID="allow" />
        <Button label="Deny" onPress={onDeny} disabled={busy} style={{ flex: 1 }} testID="deny" />
      </View>
      {onAlways ? (
        <Pressable onPress={onAlways} disabled={busy} accessibilityRole="button" style={{ minHeight: 44, justifyContent: "center", marginTop: -8, opacity: busy ? 0.45 : 1 }}>
          <Text size={13} tone="muted" style={{ textAlign: "center" }}>{alwaysLabel ?? "Don't ask again"}</Text>
        </Pressable>
      ) : null}
    </Card>
  );
}

export function QuestionCard({ question, options, onAnswer, busy }: {
  question: string; options: { label: string; description?: string }[]; onAnswer: (label: string) => void; busy?: boolean;
}) {
  const { color, radius } = useTheme();
  return (
    <Card tone="waiting">
      <View style={{ flexDirection: "row", alignItems: "flex-start", gap: 8 }}>
        <View style={{ paddingTop: 6 }}><StatusGlyph status="needs-you" size={10} /></View>
        <Text weight="semibold" style={{ flex: 1 }}>{question}</Text>
      </View>
      {options.map((o) => (
        <Pressable
          key={o.label} onPress={() => onAnswer(o.label)} disabled={busy} accessibilityRole="button" accessibilityLabel={o.label}
          style={({ pressed }) => ({
            minHeight: 44, paddingHorizontal: 12, paddingVertical: 8, borderRadius: radius.button, borderWidth: 1, borderColor: color.border,
            backgroundColor: pressed ? color.tint9 : color.panel, opacity: busy ? 0.45 : 1, justifyContent: "center", gap: 1,
          })}
        >
          <Text weight="medium" size={14.5}>{o.label}</Text>
          {o.description ? <Text size={12.5} tone="muted">{o.description}</Text> : null}
        </Pressable>
      ))}
    </Card>
  );
}
