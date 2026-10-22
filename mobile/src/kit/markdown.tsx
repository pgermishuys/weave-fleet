import { Linking, ScrollView, View } from "react-native";
import { useTheme } from "~/theme";
import { Text } from "./text";

const INLINE = /(\*\*[^*]+\*\*|`[^`]+`|\[[^\]]+\]\([^)\s]+\)|(?<![*\w])\*[^*\s][^*]*\*(?![*\w]))/g;

function Inline({ text, size = 15 }: { text: string; size?: number }) {
  const { color } = useTheme();
  return (
    <>
      {text.split(INLINE).filter(Boolean).map((part, i) => {
        if (part.startsWith("**")) return <Text key={i} size={size} weight="semibold">{part.slice(2, -2)}</Text>;
        if (part.startsWith("`")) {
          return <Text key={i} mono size={size - 1.5} style={{ backgroundColor: color.tint9 }}>{part.slice(1, -1)}</Text>;
        }
        const link = /^\[([^\]]+)\]\(([^)\s]+)\)$/.exec(part);
        if (link) {
          return (
            <Text key={i} size={size} tone="accent" style={{ textDecorationLine: "underline" }}
              onPress={() => { Linking.openURL(link[2]).catch(() => {}); }}>{link[1]}</Text>
          );
        }
        if (part.startsWith("*")) return <Text key={i} size={size} style={{ fontStyle: "italic" }}>{part.slice(1, -1)}</Text>;
        return <Text key={i} size={size}>{part}</Text>;
      })}
    </>
  );
}

type Block =
  | { kind: "code"; text: string }
  | { kind: "heading"; level: number; text: string }
  | { kind: "list"; items: { marker: string; text: string }[] }
  | { kind: "para"; text: string };

function parse(source: string): Block[] {
  const blocks: Block[] = [];
  const lines = source.replace(/\r\n/g, "\n").split("\n");
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (!line.trim()) continue;
    if (line.trimStart().startsWith("```")) {
      const body: string[] = [];
      for (i++; i < lines.length && !lines[i].trimStart().startsWith("```"); i++) body.push(lines[i]);
      blocks.push({ kind: "code", text: body.join("\n") });
      continue;
    }
    const heading = /^(#{1,4})\s+(.*)$/.exec(line);
    if (heading) { blocks.push({ kind: "heading", level: heading[1].length, text: heading[2] }); continue; }
    const item = /^\s*(\d+\.|[-*+])\s+(.*)$/.exec(line);
    if (item) {
      const last = blocks[blocks.length - 1];
      const entry = { marker: /\d/.test(item[1]) ? item[1] : "•", text: item[2] };
      if (last?.kind === "list") last.items.push(entry); else blocks.push({ kind: "list", items: [entry] });
      continue;
    }
    const last = blocks[blocks.length - 1];
    if (last?.kind === "para") last.text += " " + line.trim(); else blocks.push({ kind: "para", text: line.trim() });
  }
  return blocks;
}

const HEADING_SIZE = [0, 20, 17.5, 16, 15];

/** Agent replies: paragraphs, headings, lists, fenced code, **bold**, *italic*, `code` and links. Text is selectable. */
export function Markdown({ text }: { text: string }) {
  const { color, radius } = useTheme();
  return (
    <View style={{ gap: 10 }}>
      {parse(text).map((b, i) => {
        switch (b.kind) {
          case "code":
            return (
              <ScrollView key={i} horizontal style={{ borderRadius: radius.button, borderWidth: 1, borderColor: color.border, backgroundColor: color.codeBg }}>
                <Text mono size={12.5} selectable style={{ padding: 10, lineHeight: 19 }}>{b.text}</Text>
              </ScrollView>
            );
          case "heading":
            return <Text key={i} weight="semibold" size={HEADING_SIZE[b.level]} accessibilityRole="header" selectable style={{ marginTop: 4 }}>{b.text}</Text>;
          case "list":
            return (
              <View key={i} style={{ gap: 5 }}>
                {b.items.map((it, j) => (
                  <View key={j} style={{ flexDirection: "row", gap: 8 }}>
                    <Text tone="muted" style={{ minWidth: 18 }}>{it.marker}</Text>
                    <Text selectable style={{ flex: 1 }}><Inline text={it.text} /></Text>
                  </View>
                ))}
              </View>
            );
          default:
            return <Text key={i} selectable><Inline text={b.text} /></Text>;
        }
      })}
    </View>
  );
}
