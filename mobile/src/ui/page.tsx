// A tab's page as the mockup draws it: a panel floating on the chrome, with a large heading (and a line under it).
import type { ReactNode } from "react";
import { View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { Panel, Text } from "~/kit";

/** The chrome around a panel: the safe-area top, small side margins. `bar` is the strip above the panel. */
export function PanelPage({ bar, children }: { bar?: ReactNode; children: ReactNode }) {
  const insets = useSafeAreaInsets();
  return (
    <View style={{ flex: 1, paddingTop: insets.top + 4 }}>
      {bar}
      <Panel style={{ flex: 1, marginHorizontal: 6, marginBottom: 6 }}>{children}</Panel>
    </View>
  );
}

export function Heading({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <View style={{ paddingHorizontal: 16, paddingTop: 16, paddingBottom: 10, gap: 4 }}>
      <Text size={26} weight="semibold">{title}</Text>
      {children}
    </View>
  );
}
