import { useState, type ReactNode } from "react";
import { Pressable, ScrollView, View } from "react-native";
import { useTheme } from "~/theme";
import { Pill } from "./controls";
import { StatusGlyph } from "./glyph";
import { Icon, type IconName } from "./icons";
import { Text } from "./text";

type Tool = "read" | "edit" | "write" | "bash" | "search" | "web" | "task" | "other";

const TOOL_ICON: Record<Tool, IconName> = {
  read: "eye", edit: "edit", write: "file", bash: "terminal", search: "search", web: "globe", task: "sessions", other: "more",
};

/** One bordered box that folds a run of tool calls into rows. */
export function ToolBox({ summary, children, testID }: { summary?: string; children: ReactNode; testID?: string }) {
  const { color, radius } = useTheme();
  return (
    <View testID={testID} style={{ padding: 3, borderWidth: 1, borderColor: color.border, borderRadius: radius.card, backgroundColor: color.tint3 }}>
      {summary ? <Text size={12} tone="muted" style={{ paddingHorizontal: 8, paddingTop: 6, paddingBottom: 2 }}>{summary}</Text> : null}
      {children}
    </View>
  );
}

export function ToolRow({ tool, label, detail, status, adds, dels, chips, output }: {
  tool: Tool; label: string; detail?: string; status: "running" | "done" | "failed"; adds?: number; dels?: number;
  chips?: { label: string; tone: "running" | "error" | "waiting" | "muted" }[]; output?: string;
}) {
  const { color } = useTheme();
  const [open, setOpen] = useState(status === "failed" && !!output);
  const tappable = !!output;
  return (
    <View>
      <Pressable
        onPress={() => setOpen((o) => !o)} disabled={!tappable} accessibilityRole={tappable ? "button" : undefined}
        accessibilityState={tappable ? { expanded: open } : undefined}
        style={{ minHeight: 44, paddingHorizontal: 8, borderRadius: 7, flexDirection: "row", alignItems: "center", gap: 8 }}
      >
        <Icon name={TOOL_ICON[tool]} size={15} color={color.muted} />
        <Text size={13.5} weight="medium">{label}</Text>
        <Text mono size={12} tone="muted" numberOfLines={1} style={{ flex: 1, minWidth: 40 }}>{detail}</Text>
        {adds ? <Text mono size={12} tone="running">{`+${adds}`}</Text> : null}
        {dels ? <Text mono size={12} tone="error">{`−${dels}`}</Text> : null}
        {chips?.map((c) => <Pill key={c.label} label={c.label} tone={c.tone} />)}
        {status === "running" ? <StatusGlyph status="running" size={8} />
          : status === "done" ? <Icon name="check" size={14} color={color.running} />
          : <Icon name="x" size={14} color={color.error} />}
      </Pressable>
      {open && output ? (
        <ScrollView horizontal style={{ marginHorizontal: 6, marginBottom: 6, borderRadius: 7, borderWidth: 1, borderColor: color.border, backgroundColor: color.codeBg }}>
          <Text mono size={11.5} selectable style={{ padding: 9, lineHeight: 18 }}>{output}</Text>
        </ScrollView>
      ) : null}
    </View>
  );
}
