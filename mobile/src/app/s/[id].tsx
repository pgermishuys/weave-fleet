// One session: the conversation (the web client's reducer + the phone's folded tool rows), and a dock that is the
// composer, or the agent's pending ask (permission or question) when there is one.
import { router, useLocalSearchParams } from "expo-router";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { KeyboardAvoidingView, Platform, Pressable, ScrollView, StyleSheet, TextInput, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { isStreamWorking } from "@fleet/lib/domain-event-reducer";
import { foldMessages, stepRow, visibleSteps, type PhoneBlock } from "@fleet/lib/phone/fold-steps";
import { chooseDock, pendingQuestion } from "@fleet/lib/phone/dock-state";
import { dontAskAgain, permissionTitle } from "@fleet/lib/phone/asks";
import { duration } from "@fleet/lib/phone/time";
import { Markdown } from "~/components/Markdown";
import { Button, Panel, StatusGlyph, T } from "~/components/ui";
import { fleet, type PermissionAsk } from "~/fleet/api";
import { useSessionStream } from "~/fleet/use-session-stream";
import { font, radius, usePalette } from "~/fleet/theme";

export default function Session() {
  const { id } = useLocalSearchParams<{ id: string }>();
  const p = usePalette();
  const insets = useSafeAreaInsets();
  const scroll = useRef<ScrollView>(null);
  const [permissions, setPermissions] = useState<PermissionAsk[]>([]);
  const [draft, setDraft] = useState("");
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [turnStartedAt, setTurnStartedAt] = useState<number | null>(null);
  const [now, setNow] = useState(Date.now());

  const refreshAsks = useCallback(() => void fleet.permissions(id).then(setPermissions).catch(() => {}), [id]);
  const { state, title } = useSessionStream(id, (event) => {
    if (event.type.startsWith("permission.") || event.type === "activity_status") refreshAsks();
  });

  useEffect(() => {
    refreshAsks();
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [refreshAsks]);

  const messages = state?.messages ?? [];
  const blocks = useMemo(() => foldMessages(messages), [messages]);
  const working = state ? isStreamWorking(state.sessionStatus) : false;
  const dock = chooseDock({ permissions, question: pendingQuestion(messages), later: new Set() });
  const lastUser = [...messages].reverse().find((m) => m.role === "user");
  const startedAt = turnStartedAt ?? lastUser?.createdAt ?? null;
  const tone = dock.kind !== "composer" ? "needs-you" : working ? "working" : "idle";

  async function send() {
    const text = draft.trim();
    if (!text) return;
    setSending(true);
    setError(null);
    try {
      await fleet.prompt(id, text);
      setDraft("");
      setTurnStartedAt(Date.now());
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSending(false);
    }
  }

  async function act(run: () => Promise<unknown>) {
    try {
      await run();
      refreshAsks();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  return (
    <KeyboardAvoidingView behavior={Platform.OS === "ios" ? "padding" : "height"} style={{ flex: 1, paddingTop: insets.top + 8, paddingHorizontal: 8, paddingBottom: insets.bottom + 8 }}>
      <Panel>
        <View style={[s.header, { borderBottomColor: p.border }]}>
          <Pressable accessibilityLabel="Back" hitSlop={12} onPress={() => (router.canGoBack() ? router.back() : router.replace("/"))} style={{ width: 32, height: 44, justifyContent: "center" }}>
            <T size={26} style={{ lineHeight: 30 }}>‹</T>
          </Pressable>
          <View style={{ flex: 1 }}>
            <T weight="semibold" size={16} numberOfLines={1}>{title || " "}</T>
            <View style={{ flexDirection: "row", alignItems: "center", gap: 6 }}>
              <StatusGlyph tone={tone} size={9} />
              <T muted size={13}>{tone === "needs-you" ? "Needs you" : working ? `Working · ${duration(startedAt, now)}` : "Idle"}</T>
            </View>
          </View>
        </View>

        <ScrollView ref={scroll} testID="conversation" contentContainerStyle={{ padding: 16, gap: 14 }} onContentSizeChange={() => scroll.current?.scrollToEnd({ animated: true })}>
          {!state && <T muted>Loading…</T>}
          {blocks.map((block) => <Block key={block.key} block={block} />)}
          {working && dock.kind === "composer" && (
            <View style={{ flexDirection: "row", alignItems: "center", gap: 8, paddingVertical: 4 }}>
              <StatusGlyph tone="working" />
              <T muted>Working · {duration(startedAt, now)}</T>
            </View>
          )}
        </ScrollView>

        {error && <T style={{ color: p.error, paddingHorizontal: 16, paddingBottom: 6 }}>{error}</T>}

        {dock.kind === "permission" ? (
          <View style={[s.dock, { borderColor: p.waiting, backgroundColor: p.waitingDim }]}>
            <T weight="semibold">{permissionTitle(dock.ask)}</T>
            {dock.ask.title ? (
              <View style={{ backgroundColor: p.bg, borderRadius: radius.button, padding: 12 }}>
                <T mono size={13} numberOfLines={3}>$ {dock.ask.title}</T>
              </View>
            ) : null}
            <View style={{ flexDirection: "row", gap: 8 }}>
              <Button kind="primary" label="Allow once" style={{ flex: 1 }} onPress={() => void act(() => fleet.replyPermission(id, dock.ask.id, "once"))} />
              <Button kind="danger" label="Deny" style={{ flex: 1 }} onPress={() => void act(() => fleet.replyPermission(id, dock.ask.id, "reject"))} />
            </View>
            <Pressable onPress={() => void act(() => fleet.replyPermission(id, dock.ask.id, "always"))}>
              <T muted size={13}>{dontAskAgain(dock.ask).lead} {dontAskAgain(dock.ask).code ?? ""}</T>
            </Pressable>
          </View>
        ) : dock.kind === "question" ? (
          <View style={[s.dock, { borderColor: p.waiting, backgroundColor: p.waitingDim }]}>
            <T weight="semibold">{dock.pending.question.question}</T>
            <View style={{ flexDirection: "row", flexWrap: "wrap", gap: 8 }}>
              {dock.pending.question.options.map((option) => (
                <Button key={option.label} label={option.label} onPress={() => void act(() => fleet.answerQuestion(id, dock.pending.requestId, [[option.label]]))} />
              ))}
            </View>
          </View>
        ) : (
          <View style={[s.composer, { backgroundColor: p.card, borderColor: p.border }]}>
            <TextInput
              testID="composer"
              value={draft}
              onChangeText={setDraft}
              placeholder="Message the agent…"
              placeholderTextColor={p.muted}
              multiline
              style={{ flex: 1, color: p.text, fontFamily: font.regular, fontSize: 16, maxHeight: 140, paddingTop: 8, paddingBottom: 8 }}
            />
            <Pressable
              testID="send"
              accessibilityLabel={working ? "Queue" : "Send"}
              disabled={sending || !draft.trim()}
              onPress={() => void send()}
              style={({ pressed }) => ({ width: 40, height: 40, borderRadius: 20, alignItems: "center", justifyContent: "center", backgroundColor: draft.trim() ? p.accent : p.border, opacity: pressed ? 0.7 : 1 })}
            >
              <T weight="semibold" size={18} style={{ color: p.onAccent, lineHeight: 22 }}>↑</T>
            </Pressable>
          </View>
        )}
      </Panel>
    </KeyboardAvoidingView>
  );
}

function Block({ block }: { block: PhoneBlock }) {
  const p = usePalette();
  switch (block.kind) {
    case "user":
      return (
        <View style={{ alignSelf: "flex-end", maxWidth: "85%", backgroundColor: p.card, borderRadius: 14, borderWidth: StyleSheet.hairlineWidth, borderColor: p.border, paddingHorizontal: 14, paddingVertical: 10 }}>
          <T selectable>{block.text}</T>
        </View>
      );
    case "text":
      return <Markdown text={block.text} />;
    case "steps": {
      const { rows, more } = visibleSteps(block.steps);
      return (
        <View style={{ borderWidth: StyleSheet.hairlineWidth, borderColor: p.border, borderRadius: radius.card, paddingVertical: 4 }}>
          {rows.map((step) => {
            const row = stepRow(step);
            return (
              <View key={step.id} style={{ minHeight: 40, flexDirection: "row", alignItems: "center", paddingHorizontal: 12, gap: 8 }}>
                <T weight="medium">{row.label}</T>
                <T mono muted size={13} numberOfLines={1} style={{ flex: 1 }}>{row.detail}</T>
                {row.result === "running" ? <StatusGlyph tone="finished" size={8} />
                  : row.result === "failed" ? <T style={{ color: p.error }}>✕</T>
                  : row.result === "diff" ? <T mono size={12}><T mono size={12} style={{ color: p.running }}>+{row.adds}</T> <T mono size={12} style={{ color: p.error }}>−{row.dels}</T></T>
                  : <T style={{ color: p.running }}>✓</T>}
              </View>
            );
          })}
          {more.length > 0 && <T muted size={13} style={{ paddingHorizontal: 12, paddingVertical: 8 }}>{more.length} more steps</T>}
        </View>
      );
    }
    case "question":
      return (
        <View style={{ gap: 4 }}>
          <T muted size={13}>Question</T>
          <T>{block.question}</T>
          {block.answer && <T weight="medium" style={{ color: p.accent }}>→ {block.answer}</T>}
        </View>
      );
    case "shell":
      return <T mono size={13}>$ {block.view.command}</T>;
    case "subagent":
      return <T muted>{block.running ? "◌" : "✓"} {block.agent}: {block.title}</T>;
    case "error":
      return <T style={{ color: block.limit ? p.waiting : p.error }}>{block.text}</T>;
  }
}

const s = StyleSheet.create({
  header: { flexDirection: "row", alignItems: "center", gap: 6, paddingHorizontal: 12, paddingVertical: 8, borderBottomWidth: StyleSheet.hairlineWidth },
  dock: { margin: 10, padding: 14, gap: 10, borderRadius: radius.card, borderWidth: 1 },
  composer: { flexDirection: "row", alignItems: "flex-end", gap: 8, margin: 10, paddingLeft: 14, paddingRight: 6, paddingVertical: 6, borderRadius: 14, borderWidth: StyleSheet.hairlineWidth },
});
