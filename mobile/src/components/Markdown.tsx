// Just enough Markdown for agent replies in a spike: paragraphs, lists, headings, fenced code, **bold** and `code`.
// A real app would use a full renderer (and make it selectable, see the findings).
import { View } from "react-native";
import { T } from "~/components/ui";
import { radius, usePalette } from "~/fleet/theme";

function Inline({ text }: { text: string }) {
  const p = usePalette();
  const parts = text.split(/(\*\*[^*]+\*\*|`[^`]+`)/g).filter(Boolean);
  return (
    <T selectable>
      {parts.map((part, i) =>
        part.startsWith("**") ? <T key={i} weight="semibold">{part.slice(2, -2)}</T>
        : part.startsWith("`") ? <T key={i} mono size={13.5} style={{ backgroundColor: p.card }}>{part.slice(1, -1)}</T>
        : part,
      )}
    </T>
  );
}

export function Markdown({ text }: { text: string }) {
  const p = usePalette();
  const blocks = text.trim().split(/\n{2,}/);
  return (
    <View style={{ gap: 10 }}>
      {blocks.map((block, i) => {
        if (block.startsWith("```")) {
          return (
            <View key={i} style={{ backgroundColor: p.card, borderRadius: radius.card, padding: 12 }}>
              <T mono size={13}>{block.replace(/^```\w*\n?/, "").replace(/```$/, "")}</T>
            </View>
          );
        }
        const heading = /^(#{1,3})\s+(.*)$/.exec(block);
        if (heading) return <T key={i} weight="semibold" size={heading[1].length === 1 ? 18 : 16}>{heading[2]}</T>;
        const lines = block.split("\n");
        if (lines.every((line) => /^\s*(\d+\.|[-*])\s+/.test(line))) {
          return (
            <View key={i} style={{ gap: 6 }}>
              {lines.map((line, j) => {
                const [, marker, rest] = /^\s*(\d+\.|[-*])\s+(.*)$/.exec(line)!;
                return (
                  <View key={j} style={{ flexDirection: "row", gap: 8 }}>
                    <T muted style={{ minWidth: 18 }}>{/\d/.test(marker) ? marker : "•"}</T>
                    <View style={{ flex: 1 }}><Inline text={rest} /></View>
                  </View>
                );
              })}
            </View>
          );
        }
        return <Inline key={i} text={block} />;
      })}
    </View>
  );
}
