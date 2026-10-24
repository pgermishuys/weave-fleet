import { Pressable, View } from "react-native";
import { useTheme } from "~/theme";
import { Button, Card } from "./controls";
import { Icon, type IconName } from "./icons";
import { Text } from "./text";

/** Segments across the top of a sheet: Changes, Pages. */
export function Segments<T extends string>({ value, options, onChange }: {
  value: T; options: { value: T; label: string; count?: number }[]; onChange: (value: T) => void;
}) {
  const { color } = useTheme();
  return (
    <View style={{ flexDirection: "row", gap: 2, padding: 2, borderRadius: 9, backgroundColor: color.tint5 }}>
      {options.map((option) => {
        const on = option.value === value;
        return (
          <Pressable
            key={option.value} testID={`segment-${option.value}`} onPress={() => onChange(option.value)} accessibilityRole="tab"
            accessibilityState={{ selected: on }} accessibilityLabel={option.label}
            style={{ flex: 1, height: 36, borderRadius: 7, flexDirection: "row", gap: 6, alignItems: "center", justifyContent: "center", backgroundColor: on ? color.panel : "transparent", borderWidth: 1, borderColor: on ? color.border : "transparent" }}
          >
            <Text size={14} weight="medium" tone={on ? "default" : "muted"}>{option.label}</Text>
            {option.count ? <Text size={13} tone="muted">{option.count}</Text> : null}
          </Pressable>
        );
      })}
    </View>
  );
}

function PageIcon({ name, waiting }: { name: IconName; waiting?: boolean }) {
  const { color } = useTheme();
  return (
    <View style={{ width: 34, height: 34, borderRadius: 8, alignItems: "center", justifyContent: "center", backgroundColor: waiting ? color.waitingBg : color.tint5, borderWidth: 1, borderColor: waiting ? color.waitingEdge : "transparent" }}>
      <Icon name={name} size={17} color={waiting ? color.waiting : color.muted} />
    </View>
  );
}

/** A page in the session's Pages list. `waiting`: the agent waits for a pick on it. */
export function PageRow({ title, detail, age, waiting, isNew, onPress, testID }: {
  title: string; detail: string; age?: string; waiting?: boolean; isNew?: boolean; onPress: () => void; testID?: string;
}) {
  const { color } = useTheme();
  return (
    <Pressable
      onPress={onPress} testID={testID} accessibilityRole="button" accessibilityLabel={title}
      style={({ pressed }) => ({ minHeight: 56, paddingVertical: 9, paddingHorizontal: 4, flexDirection: "row", gap: 12, alignItems: "center", borderRadius: 8, backgroundColor: pressed || waiting ? color.tint3 : "transparent" })}
    >
      <PageIcon name={waiting ? "pick" : "page"} waiting={waiting} />
      <View style={{ flex: 1, gap: 2 }}>
        <Text weight="medium" numberOfLines={1}>{title}</Text>
        <Text size={12.5} tone={waiting ? "waiting" : "muted"} numberOfLines={1}>{detail}</Text>
      </View>
      {isNew ? <View style={{ width: 7, height: 7, borderRadius: 4, backgroundColor: color.accent }} /> : null}
      {age ? <Text size={12} tone="muted" style={{ fontVariant: ["tabular-nums"] }}>{age}</Text> : null}
    </Pressable>
  );
}

/** A page where the agent showed it in the conversation. Tap opens it full screen. */
export function PageCard({ title, detail, waiting, onPress, testID }: {
  title: string; detail: string; waiting?: boolean; onPress: () => void; testID?: string;
}) {
  const { color, radius } = useTheme();
  return (
    <Pressable
      onPress={onPress} testID={testID} accessibilityRole="button" accessibilityLabel={`Open page ${title}`}
      style={({ pressed }) => ({
        flexDirection: "row", alignItems: "center", gap: 12, padding: 10, borderRadius: radius.card, borderWidth: 1,
        borderColor: waiting ? color.waitingEdge : color.border, backgroundColor: waiting ? color.waitingBg : color.card, opacity: pressed ? 0.85 : 1,
      })}
    >
      <PageIcon name={waiting ? "pick" : "page"} waiting={waiting} />
      <View style={{ flex: 1, gap: 2 }}>
        <Text weight="semibold" numberOfLines={1}>{title}</Text>
        <Text size={12.5} tone={waiting ? "waiting" : "muted"} numberOfLines={1}>{detail}</Text>
      </View>
      <Icon name="chevron-right" size={18} color={color.muted} />
    </Pressable>
  );
}

/** The agent waits for a pick on a page: docked like a question, above the composer. */
export function PagePickCard({ title, onOpen }: { title: string; onOpen: () => void }) {
  return (
    <Card tone="waiting" testID="page-pick" style={{ flexDirection: "row", alignItems: "center", gap: 12 }}>
      <View style={{ flex: 1, gap: 2 }}>
        <Text size={13} weight="semibold" tone="waiting">Pick on the page</Text>
        <Text size={14} numberOfLines={1}>{title}</Text>
      </View>
      <Button label="Open page" variant="primary" onPress={onOpen} testID="page-pick-open" />
    </Card>
  );
}

/**
 * Fleet's bar under a page that waits for a pick (page-choice.ts pageChoiceBar): the page only proposes, this sends.
 * `strong` is the bold part of the sentence.
 */
export function PageChoiceBar({ text, strong, after, canSend, sending, onSend, onSkip, onReply }: {
  text: string; strong?: string; after?: string; canSend: boolean; sending?: boolean;
  onSend?: () => void; onSkip?: () => void; onReply?: () => void;
}) {
  const { color } = useTheme();
  return (
    <View testID="page-choice-bar" style={{ gap: 10, paddingHorizontal: 12, paddingTop: 10, paddingBottom: 4, borderTopWidth: 1, borderColor: color.waitingEdge, backgroundColor: color.waitingBg }}>
      <Text size={14} numberOfLines={2}>
        {text}{strong ? <Text size={14} weight="semibold">{strong}</Text> : null}{after}
      </Text>
      {onSend || onSkip ? (
        <View style={{ flexDirection: "row", gap: 8 }}>
          {onSend ? <Button label="Send pick" variant="primary" onPress={onSend} disabled={!canSend} loading={sending} testID="page-choice-send" style={{ flex: 1 }} /> : null}
          {onSkip ? <Button label="Skip" onPress={onSkip} disabled={sending} testID="page-choice-skip" /> : null}
          {onReply ? (
            <Pressable onPress={onReply} accessibilityRole="button" accessibilityLabel="Write instead" testID="page-choice-reply"
              style={{ width: 44, height: 44, borderRadius: 8, borderWidth: 1, borderColor: color.border, backgroundColor: color.panel, alignItems: "center", justifyContent: "center" }}>
              <Icon name="sessions" size={17} color={color.text} />
            </Pressable>
          ) : null}
        </View>
      ) : null}
    </View>
  );
}

/** The bar under a page that starts a reply about it. */
export function PageReplyBar({ onPress }: { onPress: () => void }) {
  const { color } = useTheme();
  return (
    <Pressable
      onPress={onPress} testID="page-reply-open" accessibilityRole="button" accessibilityLabel="Reply about this page"
      style={{ flexDirection: "row", alignItems: "center", height: 44, marginHorizontal: 12, marginVertical: 8, paddingHorizontal: 14, borderRadius: 22, borderWidth: 1, borderColor: color.tint9, backgroundColor: color.card }}
    >
      <Text tone="muted" style={{ flex: 1 }}>Reply about this page…</Text>
    </Pressable>
  );
}
