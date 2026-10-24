// The machines this phone is paired with, how each connection is doing, and how to add or forget one.
import { router } from "expo-router";
import { Alert, Platform, Pressable, ScrollView, View } from "react-native";
import { Heading, PanelPage } from "~/ui/page";
import { useFleet } from "~/fleet/hooks";
import { machines as store } from "~/fleet/machines";
import type { ConnectionState } from "~/fleet/hub";
import { Button, Card, Icon, Text } from "~/kit";
import { useTheme } from "~/theme";

const WORDS: Record<ConnectionState, string> = {
  connecting: "Connecting…",
  connected: "Connected",
  reconnecting: "Reconnecting…",
  offline: "Not reachable",
  "signed-out": "Signed out. The computer removed this phone.",
};

function confirmRemove(name: string, remove: () => void) {
  const message = `This phone forgets ${name}. Pair again from the computer to bring it back.`;
  if (Platform.OS === "web") {
    if (globalThis.confirm?.(message)) remove();
    return;
  }
  Alert.alert(`Remove ${name}?`, message, [{ text: "Cancel", style: "cancel" }, { text: "Remove", style: "destructive", onPress: remove }]);
}

export default function Machines() {
  const { machines } = useFleet();
  const { color } = useTheme();
  return (
    <PanelPage>
    <Heading title="Machines" />
    <ScrollView contentContainerStyle={{ padding: 16, paddingTop: 4, gap: 12 }}>
      {machines.map((m) => (
        <Card key={m.machineId} tone={m.state === "signed-out" ? "waiting" : "default"}>
          <View style={{ gap: 4 }}>
            <View style={{ flexDirection: "row", alignItems: "center" }}>
              <Text weight="semibold" size={16} numberOfLines={1} ellipsizeMode="middle" style={{ flex: 1 }}>{m.machineName}</Text>
              <Pressable accessibilityRole="button" accessibilityLabel={`Remove ${m.machineName}`} hitSlop={8} onPress={() => confirmRemove(m.machineName, () => void store.remove(m.machineId))} style={{ width: 36, height: 36, alignItems: "center", justifyContent: "center" }}>
                <Icon name="trash" size={18} color={color.error} />
              </Pressable>
            </View>
            <Text tone={m.state === "connected" ? "running" : m.state === "signed-out" ? "error" : "muted"} size={14}>{WORDS[m.state]}</Text>
            <Text tone="muted" mono size={12} numberOfLines={1}>{m.baseUrl}</Text>
          </View>
        </Card>
      ))}
      <Button label="Pair another machine" icon="plus" testID="pair-another" onPress={() => router.push("/pair")} />
    </ScrollView>
    </PanelPage>
  );
}
