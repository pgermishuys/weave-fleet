// Away from home: what the phone says when it can't reach a machine, and the one button for it. "Out of reach from this
// phone: atlas is only on Tailscale" with "Get Tailscale" (or "Turn on Tailscale" once the app has been used) is kept
// apart from "Off since 08:40", which has no button: the machine is off, and Tailscale can't help.
import { Linking, Platform, View } from "react-native";
import { tailscaleLink, type PairWarning, type ReachNote } from "@fleet/lib/phone-reach";
import { Button } from "./controls";
import { Text } from "./text";

/** Opens the Tailscale app's store page (the store offers Open when the app is already there). */
export function openTailscale(): void {
  void Linking.openURL(tailscaleLink(Platform.OS)).catch(() => {});
}

/** The line under a machine's name, and the button when Tailscale is the way in. */
export function ReachNoteView({ note }: { note: ReachNote }) {
  return (
    <View style={{ gap: 8 }} testID="machine-reach-note">
      <Text tone={note.kind === "tailscale-only" ? "waiting" : "muted"} size={14} testID="machine-reach-headline">
        {note.headline}
        {note.detail ? <Text tone="muted" size={14}>{`: ${note.detail}`}</Text> : null}
      </Text>
      {note.action ? <Button label={note.action.label} variant="primary" size="sm" testID="machine-tailscale-action" onPress={openTailscale} /> : null}
    </View>
  );
}

/** Before pairing a Tailscale-only machine with a phone that isn't on Tailscale. */
export function PairWarningView({ warning }: { warning: PairWarning }) {
  return (
    <View style={{ gap: 8 }} testID="pair-tailscale-warning">
      <Text weight="semibold" tone="waiting">{warning.title}</Text>
      <Text tone="muted">{warning.body}</Text>
      <Button label={warning.action.label} variant="secondary" size="sm" testID="pair-tailscale-action" onPress={openTailscale} />
    </View>
  );
}
