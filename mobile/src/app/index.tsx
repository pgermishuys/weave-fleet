// Sessions, the ones waiting on you first, then working, then the rest. Kept current by the hub's "sessions" topic.
import { Redirect, router } from "expo-router";
import { useCallback, useEffect, useMemo, useState } from "react";
import { Pressable, RefreshControl, SectionList, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { duration } from "@fleet/lib/phone/time";
import type { ActivityStatusPayload } from "@fleet/lib/domain-events";
import { Panel, StatusGlyph, T, toneOf } from "~/components/ui";
import { fleet, type SessionListItem } from "~/fleet/api";
import { currentCredentials } from "~/fleet/credentials";
import { hub } from "~/fleet/hub";
import { ROW, usePalette } from "~/fleet/theme";

const updatedAt = (item: SessionListItem) => Number(item.session.time.updated);

export default function Sessions() {
  const p = usePalette();
  const insets = useSafeAreaInsets();
  const [items, setItems] = useState<SessionListItem[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [now, setNow] = useState(Date.now());
  const credentials = currentCredentials();

  const load = useCallback(async () => {
    try {
      setItems((await fleet.sessions()).filter((item) => !item.isHidden && !item.archivedAt && !item.parentSessionId));
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => {
    if (!credentials) return;
    void load();
    const timer = setInterval(() => setNow(Date.now()), 1000);
    const stop = hub.subscribeSessions((event) => {
      if (event.type !== "activity_status") return;
      const { sessionId, activityStatus } = event.payload as ActivityStatusPayload;
      setItems((current) => current?.map((item) => (item.session.id === sessionId ? { ...item, activityStatus } : item)) ?? current);
    });
    return () => {
      clearInterval(timer);
      stop();
    };
  }, [credentials, load]);


  const sections = useMemo(() => {
    const sorted = [...(items ?? [])].sort((a, b) => updatedAt(b) - updatedAt(a));
    const by = (tone: string) => sorted.filter((item) => toneOf(item.activityStatus) === tone);
    return [
      { title: "Needs you", data: by("needs-you") },
      { title: "Working", data: by("working") },
      { title: "Recent", data: sorted.filter((item) => !["needs-you", "working"].includes(toneOf(item.activityStatus))) },
    ].filter((section) => section.data.length > 0);
  }, [items]);

  if (!credentials) return <Redirect href="/pair" />;

  return (
    <View style={{ flex: 1, paddingTop: insets.top + 8, paddingHorizontal: 8, paddingBottom: insets.bottom + 8 }}>
      <View style={{ flexDirection: "row", alignItems: "center", paddingHorizontal: 8, paddingBottom: 10, gap: 8 }}>
        <View style={{ width: 22, height: 22, borderRadius: 6, backgroundColor: p.accent }} />
        <T weight="semibold" size={17}>Fleet</T>
        <View style={{ flex: 1 }} />
        <View style={{ width: 8, height: 8, borderRadius: 4, backgroundColor: error ? p.error : p.running }} />
        <T muted size={13} numberOfLines={1} style={{ flexShrink: 1 }}>{credentials.machineName}</T>
      </View>
      <Panel>
        <SectionList
          testID="sessions"
          sections={sections}
          keyExtractor={(item) => item.session.id}
          contentContainerStyle={{ paddingBottom: 24 }}
          refreshControl={<RefreshControl refreshing={refreshing} tintColor={p.muted} onRefresh={async () => { setRefreshing(true); await load(); setRefreshing(false); }} />}
          ListHeaderComponent={
            <View style={{ padding: 20, paddingBottom: 6 }}>
              <T weight="semibold" size={30} style={{ lineHeight: 36 }}>Sessions</T>
              {error && <T style={{ color: p.error, marginTop: 6 }}>{error}</T>}
              {items?.length === 0 && <T muted style={{ marginTop: 6 }}>Nothing yet. Start a session on the computer.</T>}
            </View>
          }
          renderSectionHeader={({ section }) => (
            <View style={{ paddingHorizontal: 20, paddingTop: 16, paddingBottom: 4, backgroundColor: p.panel }}>
              <T muted weight="medium" size={13}>{section.title}  {section.data.length}</T>
            </View>
          )}
          renderItem={({ item }) => {
            const tone = toneOf(item.activityStatus);
            return (
              <Pressable
                onPress={() => router.push(`/s/${item.session.id}`)}
                style={({ pressed }) => ({ minHeight: ROW + 12, paddingHorizontal: 20, paddingVertical: 10, flexDirection: "row", gap: 12, alignItems: "center", backgroundColor: pressed ? p.card : "transparent" })}
              >
                <StatusGlyph tone={tone} />
                <View style={{ flex: 1 }}>
                  <T weight="medium" numberOfLines={1}>{item.session.title || "Untitled"}</T>
                  <T muted size={13} numberOfLines={1}>{[item.workspaceDisplayName, item.branch].filter(Boolean).join(" · ")}</T>
                </View>
                <T muted size={13}>{duration(updatedAt(item), now)}</T>
              </Pressable>
            );
          }}
        />
      </Panel>
    </View>
  );
}
