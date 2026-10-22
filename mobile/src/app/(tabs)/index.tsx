// Sessions across every paired machine: "Needs you", "Working", "Recent".
import { router } from "expo-router";
import { useEffect, useState } from "react";
import { Pressable, RefreshControl, SectionList, View } from "react-native";
import { manager, useFleet } from "~/fleet/hooks";
import { rowAge, rowDetail } from "~/fleet/session-list";
import { EmptyState, FleetMark, SectionHeader, SessionRow, Text, glyphStatusOf } from "~/kit";
import { Heading, PanelPage } from "~/ui/page";
import { useTheme } from "~/theme";
import type { ConnectionState } from "~/fleet/hub";

export default function Sessions() {
  const { color } = useTheme();
  const { machines, sections, loading } = useFleet();
  const [refreshing, setRefreshing] = useState(false);
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 30_000);
    return () => clearInterval(timer);
  }, []);

  const dotColor = (state: ConnectionState) =>
    state === "connected" ? color.running : state === "signed-out" ? color.error : state === "offline" ? color.muted : color.waiting;

  const count = sections.reduce((n, section) => n + section.data.length, 0);
  return (
    <PanelPage
      bar={
        <View style={{ paddingHorizontal: 18, paddingVertical: 10 }}>
          <Pressable onLongPress={() => router.push("/kit")} delayLongPress={600} accessibilityLabel="Fleet" style={{ alignSelf: "flex-start" }}>
            <FleetMark height={22} />
          </Pressable>
        </View>
      }
    >
      <Heading title="Fleet">
        <View style={{ flexDirection: "row", flexWrap: "wrap", alignItems: "center", columnGap: 12 }}>
          {machines.map((m) => (
            <View key={m.machineId} style={{ flexDirection: "row", alignItems: "center", gap: 6, maxWidth: "70%" }}>
              <View style={{ width: 7, height: 7, borderRadius: 4, backgroundColor: dotColor(m.state) }} />
              <Text size={12.5} tone="muted" numberOfLines={1} ellipsizeMode="middle" style={{ flexShrink: 1 }}>{m.machineName}</Text>
            </View>
          ))}
          <Text size={12.5} tone="muted">· {count} {count === 1 ? "session" : "sessions"}</Text>
        </View>
      </Heading>
      <SectionList
        testID="sessions"
        sections={sections}
        keyExtractor={(entry) => entry.key}
        stickySectionHeadersEnabled={false}
        contentContainerStyle={{ paddingBottom: 24 }}
        refreshControl={<RefreshControl refreshing={refreshing} tintColor={color.muted} onRefresh={() => { setRefreshing(true); void manager.refresh().finally(() => setRefreshing(false)); }} />}
        renderSectionHeader={({ section }) => <SectionHeader label={section.label} count={section.data.length} />}
        renderItem={({ item: entry }) => (
          <SessionRow
            testID="session-row"
            title={entry.item.session.title}
            detail={rowDetail(entry.item)}
            status={glyphStatusOf(entry.item.activityStatus, entry.item.sessionStatus)}
            age={rowAge(entry.item, now)}
            machine={machines.length > 1 ? entry.machineName : undefined}
            onPress={() => router.push(`/s/${entry.machineId}/${entry.item.session.id}`)}
          />
        )}
        ListEmptyComponent={loading ? undefined : <EmptyState title="No sessions yet" body="Start one on the computer and it shows up here." />}
      />
    </PanelPage>
  );
}
