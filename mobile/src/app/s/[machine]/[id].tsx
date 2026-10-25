// One session: the conversation (the web client's reducer + the phone's folded tool rows), and a dock that is the
// composer, or the agent's pending ask (permission, question, or a pick on a page) when there is one. The strip above
// the dock pulls up the session's Changes and Pages.
import { router, useLocalSearchParams } from "expo-router";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { FlatList, KeyboardAvoidingView, Platform, Pressable, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { isStreamWorking } from "@fleet/lib/domain-event-reducer";
import { chooseDock, pendingQuestion } from "@fleet/lib/phone/dock-state";
import { foldMessages, groupTools, stepDetail, stepRow, type FoldedStep, type PhoneItem } from "@fleet/lib/phone/fold-steps";
import { headerStatus, type HeaderTone } from "@fleet/lib/phone/session-status";
import { duration, short } from "@fleet/lib/phone/time";
import { dontAskAgainWording, permissionHeading } from "@fleet/lib/tools/permission-wording";
import { manager, useFleet, useSessionActivity, useSessionPages, useSessionStream } from "~/fleet/hooks";
import { conversationPages, waitingPage, type SessionPage } from "~/fleet/pages";
import { pagesSeen, usePagesSeen } from "~/fleet/pages-seen";
import type { PermissionAsk } from "~/fleet/api";
import {
  Composer, Icon, Panel, Markdown, PageCard, PagePickCard, PageRow, PermissionCard, QuestionCard, SectionHeader, Segments, Sheet,
  StatusGlyph, Text, ToolBox, ToolRow, UserBubble, type GlyphStatus,
} from "~/kit";
import { useTheme } from "~/theme";

const GLYPH: Record<HeaderTone, GlyphStatus> = { working: "working", "needs-you": "needs-you", finished: "done", error: "error", unreachable: "idle", idle: "idle" };
const TOOL = { read: "read", edit: "edit", run: "bash", search: "search", other: "other" } as const;
/** How close to the newest message the conversation still follows new ones (the web client's SCROLL_BOTTOM_THRESHOLD). */
const FOLLOW_THRESHOLD = 80;

export default function Session() {
  const { machine, id } = useLocalSearchParams<{ machine: string; id: string }>();
  const { color } = useTheme();
  const insets = useSafeAreaInsets();
  const { machines } = useFleet();
  const machineName = machines.find((m) => m.machineId === machine)?.machineName ?? "";
  const api = manager.apiFor(machine);
  const list = useRef<FlatList<PhoneItem>>(null);
  const [permissions, setPermissions] = useState<PermissionAsk[]>([]);
  const [draft, setDraft] = useState("");
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [panel, setPanel] = useState(false);
  const [segment, setSegment] = useState<"changes" | "pages">("changes");
  // Pages shown after the phone last opened this session's Pages are new to you (kept on the phone, as the list's
  // "N new pages" reads it). Nothing is new until that's been read.
  usePagesSeen();
  const seen = pagesSeen.get(machine, id);
  const seenAt = seen === undefined ? "\uffff" : seen?.at ?? pagesSeen.since;
  const [now, setNow] = useState(Date.now());

  const refreshAsks = useCallback(() => void api?.permissions(id).then(setPermissions).catch(() => {}), [api, id]);
  const sessionPages = useSessionPages(machine, id);
  const { state, title } = useSessionStream(machine, id, (event) => {
    if (event.type.startsWith("permission.")) refreshAsks();
    sessionPages.onEvent(event);
  }, sessionPages.reload);
  const streamStatus = state?.sessionStatus ?? null;
  // A snapshot (first load, or after a reconnect) and every status change may bring or settle an ask.
  useEffect(refreshAsks, [refreshAsks, streamStatus]);
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);

  const messages = useMemo(() => state?.messages ?? [], [state]);
  // Newest first, for a list drawn upside down: it opens on the latest message, and older ones are up the screen.
  const items = useMemo(() => groupTools(foldMessages(messages)).reverse(), [messages]);
  const working = state ? isStreamWorking(state.sessionStatus) : false;
  const dock = chooseDock({ permissions, question: pendingQuestion(messages), later: new Set() });
  const inConversation = useMemo(() => conversationPages(messages), [messages]);
  const pages = sessionPages.pages;
  const allPages = useMemo(() => [...pages, ...inConversation], [pages, inConversation]);
  const activity = useSessionActivity(machine, id, streamStatus ?? undefined);
  const pick = waitingPage(pages, activity);
  const newPages = allPages.filter((page) => page.shownAt > seenAt).length;
  const markPagesSeen = () => void pagesSeen.markSeen(machine, id, pages.length);
  const openPage = (page: SessionPage) => router.push({ pathname: "/page/[machine]/[session]", params: { machine, session: id, key: page.key } });
  const lastUser = [...messages].reverse().find((m) => m.role === "user");
  const lastMessage = messages[messages.length - 1];
  const status = headerStatus({
    sessionStatus: dock.kind !== "composer" || pick ? "waiting_input" : null,
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
    list.current?.scrollToOffset({ offset: 0, animated: true });
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
        <Pressable testID="session-back" accessibilityLabel="Back" hitSlop={8} onPress={() => (router.canGoBack() ? router.back() : router.replace("/"))} style={{ width: 44, height: 44, alignItems: "center", justifyContent: "center" }}>
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

      <FlatList
        ref={list}
        testID="conversation"
        inverted
        data={items}
        keyExtractor={(item) => item.key}
        renderItem={({ item }) => <Item item={item} pages={allPages} waiting={pick} onOpen={openPage} />}
        ItemSeparatorComponent={Gap}
        // As the web client's conversation: within 80 px of the newest message it follows new ones, further up it keeps
        // your place while they arrive.
        maintainVisibleContentPosition={{ minIndexForVisible: 0, autoscrollToTopThreshold: FOLLOW_THRESHOLD }}
        // The header sits under the newest message; flex-end keeps a short conversation at the top, not the bottom.
        ListHeaderComponent={working && dock.kind === "composer" && !pick ? (
          <View style={{ flexDirection: "row", alignItems: "center", gap: 8, paddingTop: 14 }}>
            <StatusGlyph status="working" size={10} />
            <Text tone="muted" size={13}>Working · {duration(lastUser?.createdAt ?? null, now)}</Text>
          </View>
        ) : undefined}
        ListFooterComponent={state ? undefined : <Text tone="muted">Loading…</Text>}
        style={{ flex: 1 }}
        contentContainerStyle={{ padding: 16, flexGrow: 1, justifyContent: "flex-end" }}
      />

      <Pressable
        testID="panel-open" accessibilityRole="button" accessibilityLabel="Changes and pages" onPress={() => { setSegment(newPages ? "pages" : "changes"); setPanel(true); }}
        style={{ flexDirection: "row", alignItems: "center", gap: 8, marginHorizontal: 12, paddingHorizontal: 12, height: 40, borderRadius: 10, backgroundColor: color.tint5 }}
      >
        <Icon name="file" size={16} color={color.muted} />
        <Text weight="medium" size={14}>Changes</Text>
        <View style={{ flex: 1 }} />
        {allPages.length ? (
          <View testID="pages-count" style={{ flexDirection: "row", alignItems: "center", gap: 6 }}>
            <Icon name="page" size={15} color={newPages ? color.accent : color.muted} />
            <Text weight="medium" size={14} tone={newPages ? "accent" : "default"}>Pages</Text>
            <Text size={14} tone={newPages ? "accent" : "muted"}>{allPages.length}</Text>
          </View>
        ) : null}
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
          <View style={{ gap: 10 }}>
            {pick ? <PagePickCard title={pick.title} onOpen={() => openPage(pick)} /> : null}
            <Composer value={draft} onChangeText={setDraft} onSend={() => void send()} sending={sending} working={working} machine={machines.length > 1 ? machineName : undefined} placeholder="Message the agent" />
          </View>
        )}
      </View>

      <Sheet open={panel} onClose={() => { setPanel(false); if (segment === "pages") markPagesSeen(); }} title={title} testID="panel">
        <View style={{ gap: 12 }}>
          <Segments
            value={segment}
            onChange={(next) => { if (segment === "pages") markPagesSeen(); setSegment(next); }}
            options={[{ value: "changes", label: "Changes" }, { value: "pages", label: "Pages", count: allPages.length }]}
          />
          {segment === "changes" ? (
            <Text tone="muted">Changes and Files come here</Text>
          ) : allPages.length === 0 ? (
            <Text tone="muted">Pages the agent shows come here.</Text>
          ) : (
            <View>
              {[...pages].reverse().map((page) => (
                <PageRow
                  key={page.key} testID="page-row" title={page.title} age={clock(page.shownAt)} isNew={page.shownAt > seenAt}
                  waiting={page.key === pick?.key} detail={pageDetail(page, page.key === pick?.key)}
                  onPress={() => { markPagesSeen(); setPanel(false); openPage(page); }}
                />
              ))}
              {inConversation.length ? (
                <>
                  <View style={{ marginHorizontal: -16 }}><SectionHeader label="In the conversation" /></View>
                  {[...inConversation].reverse().map((page) => (
                    <PageRow key={page.key} testID="page-row" title={page.title} age={clock(page.shownAt)} detail="Shown in the conversation" isNew={page.shownAt > seenAt} onPress={() => { markPagesSeen(); setPanel(false); openPage(page); }} />
                  ))}
                </>
              ) : null}
            </View>
          )}
        </View>
      </Sheet>
      </Panel>
    </KeyboardAvoidingView>
  );
}

function Gap() {
  return <View style={{ height: 14 }} />;
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

/** "10:31", or "Mon 10:31" for an earlier day. */
function clock(iso: string): string {
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return "";
  const time = at.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  return at.toDateString() === new Date().toDateString() ? time : `${at.toLocaleDateString([], { weekday: "short" })} ${time}`;
}

function pageDetail(page: SessionPage, waiting: boolean): string {
  if (waiting) return "Waiting for your pick";
  if (page.choice?.status === "answered") return `Picked ${page.choice.summary ?? page.choice.value ?? ""}`.trim();
  if (page.choice?.status === "not-answered") return "Not picked";
  return "Page";
}

/** The page a fleet_page_show call showed: in the conversation (its call), or in a canvas (the metadata's canvas id, or the title). */
function stepPage(step: FoldedStep, pages: readonly SessionPage[]): SessionPage | undefined {
  if (step.tool !== "fleet_page_show" && !step.tool.endsWith("_fleet_page_show")) return undefined;
  const state = (step.part.state ?? {}) as { input?: { title?: unknown }; metadata?: { canvasId?: unknown } };
  return pages.find((page) => page.key === `call:${step.part.callId}`)
    ?? pages.find((page) => page.key === state.metadata?.canvasId)
    ?? pages.find((page) => page.place === "tab" && page.title === state.input?.title);
}

function Item({ item, pages, waiting, onOpen }: { item: PhoneItem; pages: readonly SessionPage[]; waiting: SessionPage | null; onOpen: (page: SessionPage) => void }) {
  switch (item.kind) {
    case "user":
      return <UserBubble text={item.text} />;
    case "text":
      return <Markdown text={item.text} />;
    case "tools": {
      // Each page the calls showed gets a card under them, as the desktop draws it under its tool row.
      const shown = [...new Map(item.parts.flatMap((part) => (part.kind === "steps" ? part.steps : []))
        .map((step) => stepPage(step, pages)).filter((page): page is SessionPage => !!page)
        .map((page) => [page.key, page])).values()];
      return (
        <View style={{ gap: 10 }}>
          <ToolBox summary={item.parts.find((p) => p.kind === "steps")?.summary}>
            {item.parts.map((part) => part.kind === "steps"
              ? part.steps.map(toolRow)
              : <ToolRow key={part.key} tool="task" label={part.agent} detail={part.title} status={part.running ? "running" : "done"} />)}
          </ToolBox>
          {shown.map((page) => (
            <PageCard
              key={page.key} testID="page-card" title={page.title} waiting={page.key === waiting?.key}
              detail={page.key === waiting?.key ? "Waiting for your pick" : `${page.place === "conversation" ? "Shown in the conversation" : pageDetail(page, false)} · ${clock(page.shownAt)}`}
              onPress={() => onOpen(page)}
            />
          ))}
        </View>
      );
    }
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
