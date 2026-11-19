// "I don't have Fleet yet": the phone can't run Fleet, so it says where Fleet runs and gives the two ways to start.
// "Send me the link" opens the share sheet with the download page (to the computer); "Share the line" opens the share sheet with the one
// install line for a machine with no screen (the sheet offers Copy, or sends it to the machine).
import { Share, View } from "react-native";
import { downloadShare, installShare, INSTALL_LINE_NOTE } from "@fleet/lib/phone-first";
import { Button, Card } from "./controls";
import { Text } from "./text";

export function NoFleetYet({ onShare, onShareLine }: { onShare?: () => void; onShareLine?: () => void }) {
  const share = onShare ?? (() => { void Share.share(downloadShare()).catch(() => {}); });
  const shareLine = onShareLine ?? (() => { void Share.share(installShare()).catch(() => {}); });
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
        <Button label="Share the line" size="sm" icon="external-link" testID="share-line" onPress={shareLine} />
        <Text size={14} tone="muted" testID="share-line-note">{INSTALL_LINE_NOTE}</Text>
      </Card>
      <Text size={14} tone="muted">When it is running it shows a code. Come back here and scan it.</Text>
    </View>
  );
}
