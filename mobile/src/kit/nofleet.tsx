// "I don't have Fleet yet": the phone can't run Fleet, so it says where Fleet runs and gives the two ways to start.
// "Send me the link" opens the share sheet with the download page (to the computer); "Copy the line" puts the one
// install line for a machine with no screen on the clipboard.
import * as Clipboard from "expo-clipboard";
import { useState } from "react";
import { Share, View } from "react-native";
import { downloadShare, FLEET_INSTALL_LINE, INSTALL_LINE_NOTE } from "@fleet/lib/phone-first";
import { Button, Card } from "./controls";
import { Text } from "./text";

export function NoFleetYet({ onShare, onCopy }: { onShare?: () => void; onCopy?: () => void }) {
  const [copied, setCopied] = useState(false);
  const share = onShare ?? (() => { void Share.share(downloadShare()).catch(() => {}); });
  const copy = onCopy ?? (() => { void Clipboard.setStringAsync(FLEET_INSTALL_LINE); });
  return (
    <View style={{ gap: 12 }} testID="no-fleet-yet">
      <Text size={28} weight="bold">Where will Fleet run?</Text>
      <Text tone="muted">Your agents run there. This phone looks at it.</Text>
      <Card>
        <Text weight="semibold">On my computer</Text>
        <Text size={14} tone="muted">Mac, Windows or Linux. Get the desktop app.</Text>
        <Button label="Send me the link" size="sm" icon="external-link" testID="send-link" onPress={share} />
      </Card>
      <Card>
        <Text weight="semibold">On a server</Text>
        <Text size={14} tone="muted">A machine you reach by terminal. One line installs it.</Text>
        <Button label={copied ? "Copied" : "Copy the line"} size="sm" icon={copied ? "check" : undefined} testID="copy-line" onPress={() => { copy(); setCopied(true); }} />
        <Text size={14} tone="muted" testID="copy-note">{INSTALL_LINE_NOTE}</Text>
      </Card>
      <Text size={14} tone="muted">When it is running it shows a code. Come back here and scan it.</Text>
    </View>
  );
}
