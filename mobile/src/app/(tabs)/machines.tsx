// The machines this phone is paired with, how each connection is doing (and over which of its addresses), and how to
// add or forget one.
import { router } from "expo-router";
import { useState } from "react";
import { Alert, Platform, Pressable, ScrollView, View } from "react-native";
import { Heading, PanelPage } from "~/ui/page";
import { manager, useFleet } from "~/fleet/hooks";
import { machines as store } from "~/fleet/machines";
import type { ConnectionState } from "~/fleet/hub";
import { moveAddress } from "~/fleet/routes-core";
import { AddressList, Button, Card, Icon, RouteLine, Text } from "~/kit";
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
  const [saving, setSaving] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});

  async function move(machineId: string, urls: string[], from: number, to: number) {
    setSaving(machineId);
    setErrors((e) => ({ ...e, [machineId]: "" }));
    try {
      await manager.reorder(machineId, moveAddress(urls, from, to));
    } catch (error) {
      setErrors((e) => ({ ...e, [machineId]: error instanceof Error ? error.message : String(error) }));
    } finally {
      setSaving(null);
    }
  }

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
            {m.state === "connected" && m.addresses.length > 1
              ? <RouteLine addresses={m.addresses} inUse={m.baseUrl} />
              : <Text tone={m.state === "connected" ? "running" : m.state === "signed-out" ? "error" : "muted"} size={14}>{WORDS[m.state]}</Text>}
          </View>
          <AddressList
            addresses={m.addresses} inUse={m.baseUrl} busy={saving === m.machineId}
            onMove={(from, to) => void move(m.machineId, m.addresses.map((a) => a.url), from, to)}
          />
          {errors[m.machineId] ? <Text tone="error" size={13}>{errors[m.machineId]}</Text> : null}
        </Card>
      ))}
      <Button label="Pair another machine" icon="plus" testID="pair-another" onPress={() => router.push("/pair")} />
    </ScrollView>
    </PanelPage>
  );
}
