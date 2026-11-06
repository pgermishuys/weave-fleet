import { Pressable, View } from "react-native";
import { useTheme } from "~/theme";
import { Button, Card } from "./controls";
import { Icon } from "./icons";
import { Text } from "./text";

/** The top of the sessions list after a restart: "Fleet restarted. 2 sessions were cut off." Tap to see them; "Stop all" when Fleet is continuing some. */
export function InterruptionBanner({ text, onPress, onStopAll, busy, testID }: {
  text: string; onPress: () => void; onStopAll?: () => void; busy?: boolean; testID?: string;
}) {
  const { color, radius } = useTheme();
  return (
    <View testID={testID} style={{ flexDirection: "row", alignItems: "center", gap: 8, marginHorizontal: 12, marginBottom: 8, paddingLeft: 12, paddingRight: onStopAll ? 8 : 12, minHeight: 48, borderRadius: radius.card, borderWidth: 1, borderColor: color.waitingEdge, backgroundColor: color.waitingBg }}>
      <Pressable onPress={onPress} accessibilityRole="button" accessibilityLabel={text} style={{ flex: 1, flexDirection: "row", alignItems: "center", gap: 8, minHeight: 48 }}>
        <Icon name="alert" size={16} color={color.waiting} />
        <Text size={13.5} style={{ flex: 1 }}>{text}</Text>
        {onStopAll ? null : <Icon name="chevron-right" size={16} color={color.muted} />}
      </Pressable>
      {onStopAll ? <Button label="Stop all" size="sm" onPress={onStopAll} loading={busy} testID="stop-all" /> : null}
    </View>
  );
}

/** In a conversation, when a restart cut the session's turn off. `auto`: Fleet is continuing it, so there is no Continue. */
export function InterruptedCard({ waiting, auto, lastCall, changedFiles, onContinue, onDismiss, busy, testID }: {
  waiting: boolean; auto: boolean; lastCall?: string | null; changedFiles?: number | null;
  onContinue: () => void; onDismiss: () => void; busy?: boolean; testID?: string;
}) {
  return (
    <Card tone="waiting" testID={testID}>
      <Text weight="semibold">{waiting ? "Cut off · question lost" : "Cut off by a restart"}</Text>
      {auto ? <Text size={13}>Fleet is continuing this by itself.</Text> : null}
      {lastCall ? <Text size={13} tone="muted">Last call that didn't finish: <Text size={13} weight="semibold">{lastCall}</Text></Text> : null}
      {changedFiles ? <Text size={13} tone="muted">{`${changedFiles} ${changedFiles === 1 ? "file" : "files"} changed in this worktree, still on disk.`}</Text> : null}
      <View style={{ flexDirection: "row", gap: 8 }}>
        {auto ? null : <Button label="Continue" variant="primary" size="sm" onPress={onContinue} loading={busy} testID="interruption-continue" />}
        <Button label="Dismiss" size="sm" onPress={onDismiss} disabled={busy} testID="interruption-dismiss" />
      </View>
    </Card>
  );
}
