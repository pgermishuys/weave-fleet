// Sessions across every paired machine: "Needs you", "Working", "Recent".
import { router } from "expo-router";
import { useEffect, useState } from "react";
import { Pressable, RefreshControl, SectionList, View } from "react-native";
import { manager, useFleet } from "~/fleet/hooks";
import { newPagesOf, pagesSeen, usePagesSeen } from "~/fleet/pages-seen";
import { interruptionBanner } from "~/fleet/interruption-core";
import { rowAge, rowDetail } from "~/fleet/session-list";
import { Button, EmptyState, FleetMark, InterruptionBanner, SectionHeader, SessionRow, Sheet, Text, glyphStatusOf } from "~/kit";
import { Heading, PanelPage } from "~/ui/page";
import { useTheme } from "~/theme";
import type { ConnectionState } from "~/fleet/hub";

export default function Sessions() {
  const { color } = useTheme();
  const { machines, sections, loading, interrupted } = useFleet();
  const [cutOff, setCutOff] = useState(false);
  const [stopping, setStopping] = useState(false);
  const banner = interruptionBanner(interrupted);
  const [refreshing, setRefreshing] = useState(false);
  const [now, setNow] = useState(Date.now());
  usePagesSeen();
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
        <View style={{ paddingHorizontal: 18, paddingVertical: 6, flexDirection: "row", alignItems: "center", justifyContent: "space-between" }}>
          <Pressable onLongPress={() => router.push("/kit")} delayLongPress={600} accessibilityLabel="Fleet" testID="fleet-mark" style={{ alignSelf: "flex-start" }}>
            <FleetMark height={22} />
          </Pressable>
          <Button label="New session" icon="plus" variant="primary" size="sm" testID="new-session" onPress={() => router.push("/new")} disabled={machines.length === 0} />
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
        ListHeaderComponent={banner ? (
          <InterruptionBanner
            testID="interruption-banner" text={banner.text} onPress={() => setCutOff(true)} busy={stopping}
            onStopAll={banner.automatic ? () => { setStopping(true); void manager.stopAutomatic().catch(() => {}).finally(() => setStopping(false)); } : undefined}
          />
        ) : undefined}
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
            newPages={entry.item.pageCount ? newPagesOf(entry.item, pagesSeen.get(entry.machineId, entry.item.session.id), pagesSeen.since) : 0}
            onPress={() => router.push(`/s/${entry.machineId}/${entry.item.session.id}`)}
          />
        )}
        ListEmptyComponent={loading ? undefined : <EmptyState title="No sessions yet" body="Start one on the computer and it shows up here." />}
      />
      <Sheet open={cutOff && interrupted.length > 0} onClose={() => setCutOff(false)} title="Cut off by the restart" testID="interrupted-sheet">
        <View style={{ marginHorizontal: -16 }}>
          {interrupted.map((entry) => (
            <SessionRow
              key={entry.key} testID="interrupted-row" title={entry.item.session.title}
              detail={entry.interruption.auto ? "Fleet is continuing this" : entry.interruption.waiting ? "Question lost" : rowDetail(entry.item)}
              status="idle" age={rowAge(entry.item, now)} machine={machines.length > 1 ? entry.machineName : undefined}
              onPress={() => { setCutOff(false); router.push(`/s/${entry.machineId}/${entry.item.session.id}`); }}
            />
          ))}
        </View>
      </Sheet>
    </PanelPage>
  );
}
