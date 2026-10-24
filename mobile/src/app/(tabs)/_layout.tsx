import { Redirect } from "expo-router";
import { TabList, TabSlot, TabTrigger, Tabs, type TabTriggerSlotProps } from "expo-router/ui";
import { Pressable, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useFleet } from "~/fleet/hooks";
import { machines, useMachines } from "~/fleet/machines";
import { Icon, Text, type IconName } from "~/kit";
import { useTheme } from "~/theme";

const TABS: { name: string; href: "/" | "/workflows" | "/automations" | "/machines"; label: string; icon: IconName }[] = [
  { name: "index", href: "/", label: "Sessions", icon: "sessions" },
  { name: "workflows", href: "/workflows", label: "Workflows", icon: "workflows" },
  { name: "automations", href: "/automations", label: "Automations", icon: "automations" },
  { name: "machines", href: "/machines", label: "Machines", icon: "machines" },
];

function TabButton({ label, icon, dot, isFocused, onPress }: TabTriggerSlotProps & { label: string; icon: IconName; dot?: boolean }) {
  const { color } = useTheme();
  const tint = isFocused ? color.accent : color.muted;
  return (
    <Pressable onPress={onPress} accessibilityRole="button" accessibilityLabel={label} accessibilityState={{ selected: !!isFocused }} style={{ flex: 1, minHeight: 56, alignItems: "center", justifyContent: "center", gap: 3 }}>
      <View>
        <Icon name={icon} size={22} color={tint} />
        {dot && <View style={{ position: "absolute", top: -2, right: -5, width: 9, height: 9, borderRadius: 5, backgroundColor: color.waiting }} />}
      </View>
      <Text size={11} weight={isFocused ? "semibold" : "medium"} style={{ color: tint }}>{label}</Text>
    </Pressable>
  );
}

export default function TabsLayout() {
  const { color } = useTheme();
  const insets = useSafeAreaInsets();
  const paired = useMachines();
  const { sections } = useFleet();
  const waiting = sections.some((section) => section.key === "needs-you");
  // Until the keychain has been read there's nothing to decide.
  if (machines.loaded && paired.length === 0) return <Redirect href="/pair" />;
  return (
    <Tabs>
      <TabSlot />
      <TabList style={{ paddingBottom: insets.bottom, backgroundColor: color.chrome, borderTopWidth: 1, borderTopColor: color.border }}>
        {TABS.map((tab) => (
          <TabTrigger key={tab.name} name={tab.name} href={tab.href} asChild>
            <TabButton label={tab.label} icon={tab.icon} dot={tab.name === "index" && waiting} />
          </TabTrigger>
        ))}
      </TabList>
    </Tabs>
  );
}
