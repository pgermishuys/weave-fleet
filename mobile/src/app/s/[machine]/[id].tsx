// One session: the conversation (the web client's reducer + the phone's folded tool rows), and a dock that is the
// composer, or the agent's pending ask (permission or question) when there is one.
import { router, useLocalSearchParams } from "expo-router";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { KeyboardAvoidingView, Platform, Pressable, ScrollView, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { isStreamWorking } from "@fleet/lib/domain-event-reducer";
import { chooseDock, pendingQuestion } from "@fleet/lib/phone/dock-state";
import { foldMessages, groupTools, stepDetail, stepRow, type FoldedStep, type PhoneItem } from "@fleet/lib/phone/fold-steps";
import { headerStatus, type HeaderTone } from "@fleet/lib/phone/session-status";
import { duration, short } from "@fleet/lib/phone/time";
import { dontAskAgainWording, permissionHeading } from "@fleet/lib/tools/permission-wording";
import { manager, useFleet, useSessionStream } from "~/fleet/hooks";
import type { PermissionAsk } from "~/fleet/api";
import { Composer, Icon, Panel, Markdown, PermissionCard, QuestionCard, Sheet, StatusGlyph, Text, ToolBox, ToolRow, UserBubble, type GlyphStatus } from "~/kit";
import { useTheme } from "~/theme";

const GLYPH: Record<HeaderTone, GlyphStatus> = { working: "working", "needs-you": "needs-you", finished: "done", error: "error", unreachable: "idle", idle: "idle" };
const TOOL = { read: "read", edit: "edit", run: "bash", search: "search", other: "other" } as const;

export default function Session() {
  const { machine, id } = useLocalSearchParams<{ machine: string; id: string }>();
  const { color } = useTheme();
  const insets = useSafeAreaInsets();
  const { machines } = useFleet();
  const machineName = machines.find((m) => m.machineId === machine)?.machineName ?? "";
  const api = manager.apiFor(machine);
  const scroll = useRef<{ scrollToEnd: (options?: { animated?: boolean }) => void } | null>(null);
  const [permissions, setPermissions] = useState<PermissionAsk[]>([]);
  const [draft, setDraft] = useState("");
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [panel, setPanel] = useState(false);
  const [now, setNow] = useState(Date.now());

  const refreshAsks = useCallback(() => void api?.permissions(id).then(setPermissions).catch(() => {}), [api, id]);
  const { state, title } = useSessionStream(machine, id, (event) => {
    if (event.type.startsWith("permission.")) refreshAsks();
  });
  const streamStatus = state?.sessionStatus ?? null;
  // A snapshot (first load, or after a reconnect) and every status change may bring or settle an ask.
  useEffect(refreshAsks, [refreshAsks, streamStatus]);
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);

  const messages = useMemo(() => state?.messages ?? [], [state]);
  const items = useMemo(() => groupTools(foldMessages(messages)), [messages]);
  const working = state ? isStreamWorking(state.sessionStatus) : false;
  const dock = chooseDock({ permissions, question: pendingQuestion(messages), later: new Set() });
  const lastUser = [...messages].reverse().find((m) => m.role === "user");
  const lastMessage = messages[messages.length - 1];
  const status = headerStatus({
    sessionStatus: dock.kind !== "composer" ? "waiting_input" : null,
    streamStatus,
    turnStartedAt: lastUser?.createdAt ?? null,
    updatedAt: lastMessage?.createdAt ?? null,
    lastMessageAt: lastMessage?.createdAt ?? null,
    hasMessages: messages.length > 0,
    unreachableSince: null,
    now,
  });
  const statusLine = [status.state, status.detail, machineName].filter(Boolean).join(" · ");

  async function act(run: () => Promise<unknown> | undefined) {
    try {
      await run();
      refreshAsks();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  async function send() {
    const text = draft.trim();
    if (!text) return;
    setSending(true);
    setError(null);
    try {
      await api?.prompt(id, text);
      setDraft("");
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSending(false);
    }
  }

  return (
    <KeyboardAvoidingView behavior={Platform.OS === "ios" ? "padding" : "height"} style={{ flex: 1, paddingTop: insets.top + 4, paddingBottom: insets.bottom + 6, paddingHorizontal: 6 }}>
      <Panel style={{ flex: 1 }}>
      <View style={{ flexDirection: "row", alignItems: "center", gap: 4, paddingHorizontal: 8, paddingVertical: 8 }}>
        <Pressable accessibilityLabel="Back" hitSlop={8} onPress={() => (router.canGoBack() ? router.back() : router.replace("/"))} style={{ width: 44, height: 44, alignItems: "center", justifyContent: "center" }}>
          <Icon name="back" size={22} color={color.text} />
        </Pressable>
        <View style={{ flex: 1, gap: 2 }}>
          <Text weight="semibold" size={16} numberOfLines={1}>{title || " "}</Text>
          <View style={{ flexDirection: "row", alignItems: "center", gap: 7 }}>
            <StatusGlyph status={GLYPH[status.tone]} size={10} />
            <Text tone={status.tone === "needs-you" ? "waiting" : status.tone === "error" ? "error" : "muted"} size={13} numberOfLines={1} style={{ flexShrink: 1 }}>{statusLine}</Text>
          </View>
        </View>
      </View>

      <ScrollView
        ref={(instance) => { scroll.current = instance; }}
        testID="conversation"
        style={{ flex: 1 }}
        contentContainerStyle={{ padding: 16, gap: 14 }}
        onContentSizeChange={() => scroll.current?.scrollToEnd({ animated: true })}
      >
        {!state && <Text tone="muted">Loading…</Text>}
        {items.map((item) => <Item key={item.key} item={item} />)}
        {working && dock.kind === "composer" && (
          <View style={{ flexDirection: "row", alignItems: "center", gap: 8 }}>
            <StatusGlyph status="working" size={10} />
            <Text tone="muted" size={13}>Working · {duration(lastUser?.createdAt ?? null, now)}</Text>
          </View>
        )}
      </ScrollView>

      <Pressable
        testID="panel-open" accessibilityRole="button" accessibilityLabel="Changes" onPress={() => setPanel(true)}
        style={{ flexDirection: "row", alignItems: "center", gap: 8, marginHorizontal: 12, paddingHorizontal: 12, height: 40, borderRadius: 10, backgroundColor: color.tint5 }}
      >
        <Icon name="file" size={16} color={color.muted} />
        <Text weight="medium" size={14} style={{ flex: 1 }}>Changes</Text>
        <Icon name="chevron-up" size={16} color={color.muted} />
      </Pressable>

      {error && <Text tone="error" size={13} style={{ paddingHorizontal: 16, paddingTop: 6 }}>{error}</Text>}

      <View style={{ padding: 12 }}>
        {dock.kind === "permission" ? (
          <PermissionCard
            testID="permission"
            title={permissionHeading(dock.ask)}
            session={title}
            machine={machines.length > 1 ? machineName : undefined}
            age={short(Date.parse(dock.ask.askedAt), now)}
            command={dock.ask.title ?? undefined}
            alwaysLabel={(({ lead, code }) => (code ? `${lead} ${code}` : lead))(dontAskAgainWording(dock.ask))}
            onAllow={() => void act(() => api?.replyPermission(id, dock.ask.id, "once"))}
            onDeny={() => void act(() => api?.replyPermission(id, dock.ask.id, "reject"))}
            onAlways={() => void act(() => api?.replyPermission(id, dock.ask.id, "always"))}
          />
        ) : dock.kind === "question" ? (
          <QuestionCard
            question={dock.pending.question.question}
            options={dock.pending.question.options.map((o) => ({ label: o.label, description: o.description || undefined }))}
            onAnswer={(label) => void act(() => api?.answerQuestion(id, dock.pending.requestId, [[label]]))}
          />
        ) : (
          <Composer value={draft} onChangeText={setDraft} onSend={() => void send()} sending={sending} working={working} machine={machines.length > 1 ? machineName : undefined} placeholder="Message the agent" />
        )}
      </View>

      <Sheet open={panel} onClose={() => setPanel(false)} title="Changes" testID="panel">
        <Text tone="muted">Changes, Files and Pages come here</Text>
      </Sheet>
      </Panel>
    </KeyboardAvoidingView>
  );
}

function toolRow(step: FoldedStep) {
  const row = stepRow(step);
  const detail = row.result === "running" ? undefined : stepDetail(step);
  return (
    <ToolRow
      key={step.id}
      tool={TOOL[step.category]}
      label={row.label}
      detail={row.detail}
      status={row.result === "running" ? "running" : row.result === "failed" ? "failed" : "done"}
      adds={row.result === "diff" ? row.adds : undefined}
      dels={row.result === "diff" ? row.dels : undefined}
      output={detail?.kind === "output" ? detail.text : undefined}
    />
  );
}

function Item({ item }: { item: PhoneItem }) {
  switch (item.kind) {
    case "user":
      return <UserBubble text={item.text} />;
    case "text":
      return <Markdown text={item.text} />;
    case "tools":
      return (
        <ToolBox summary={item.parts.find((p) => p.kind === "steps")?.summary}>
          {item.parts.map((part) => part.kind === "steps"
            ? part.steps.map(toolRow)
            : <ToolRow key={part.key} tool="task" label={part.agent} detail={part.title} status={part.running ? "running" : "done"} />)}
        </ToolBox>
      );
    case "question":
      return (
        <View style={{ gap: 2 }}>
          <Text tone="muted" size={13}>{item.question}</Text>
          {item.answer ? <Text tone="accent" weight="medium">{item.answer}</Text> : null}
        </View>
      );
    case "shell":
      return <ToolRow tool="bash" label="Shell" detail={item.view.command} status={item.view.state === "running" ? "running" : item.view.state === "done" ? "done" : "failed"} />;
    case "error":
      return <Text tone={item.limit ? "waiting" : "error"}>{item.text}</Text>;
  }
}
