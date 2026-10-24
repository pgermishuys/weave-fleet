// A page an agent showed (fleet_page_show), full screen: Fleet's thin bar, the page edge to edge in a WebView on the
// page's own address, then the bar that sends a pick (when the agent waits for one) or starts a reply about the page.
// Swipe in from an edge, or along the bar, for the session's other pages; each one loads when it's shown.
import * as Linking from "expo-linking";
import { router, useLocalSearchParams } from "expo-router";
import { useEffect, useMemo, useRef, useState, type ComponentType, type Ref } from "react";
import { KeyboardAvoidingView, PanResponder, Platform, Pressable, View, type StyleProp, type ViewStyle } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { WebView, type WebViewMessageEvent } from "react-native-webview";
import { keepPageState, pageStateMessage } from "@fleet/lib/page-bridge";
import { isChoiceLive, pageChoiceBar } from "@fleet/lib/page-choice";
import { pageThemeMessage } from "@fleet/lib/page-theme-core";
import { manager, useFleet, useSessionActivity, useSessionPages, useSessionStream } from "~/fleet/hooks";
import { conversationPages, pagePrompt, pageShim, pageTheme, pageUrl, postToPage, readBridgeMessage } from "~/fleet/pages";
import { Button, Composer, Icon, PageChoiceBar, PageReplyBar, Sheet, Text } from "~/kit";
import { useTheme } from "~/theme";

// react-native-webview's own props type is the iOS, Android and Windows props intersected, which comes out as never with
// this React Native's types; these are the props this screen uses.
const PageWebView = WebView as unknown as ComponentType<{
  ref?: Ref<WebView>; testID?: string; source: { uri: string }; injectedJavaScriptBeforeContentLoaded: string;
  onMessage: (event: WebViewMessageEvent) => void; onError: () => void; onHttpError: () => void; originWhitelist: string[];
  setSupportMultipleWindows: boolean; style?: StyleProp<ViewStyle>;
}>;

export default function PageScreen() {
  const { machine, session, key } = useLocalSearchParams<{ machine: string; session: string; key: string }>();
  const { color, scheme } = useTheme();
  const insets = useSafeAreaInsets();
  const { machines } = useFleet();
  const machineView = machines.find((m) => m.machineId === machine);
  const api = manager.apiFor(machine);
  const { pages: canvasPages, reload, onEvent } = useSessionPages(machine, session);
  const { state, title: sessionTitle } = useSessionStream(machine, session, onEvent, reload);
  const pages = useMemo(() => [...canvasPages, ...conversationPages(state?.messages ?? [])], [canvasPages, state]);
  const [currentKey, setCurrentKey] = useState(key);
  const index = Math.max(0, pages.findIndex((p) => p.key === currentKey));
  const page = pages[index] ?? null;

  const web = useRef<WebView>(null);
  const [reloads, setReloads] = useState(0);
  const [failed, setFailed] = useState(false);
  const [menu, setMenu] = useState(false);
  const [proposal, setProposal] = useState<{ value: string; summary: string } | null>(null);
  const [sending, setSending] = useState(false);
  const [reply, setReply] = useState<string | null>(null);
  const [replySending, setReplySending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const theme = useMemo(() => pageTheme(scheme), [scheme]);
  const shim = useMemo(() => pageShim(theme), [theme]);
  const online = machineView?.state === "connected";
  const activity = useSessionActivity(machine, session, state?.sessionStatus);
  const live = isChoiceLive(page?.choice, activity);
  const bar = page?.choice ? pageChoiceBar(page.choice, live, proposal) : null;

  // The theme changed while the page is open: the page follows without a reload.
  useEffect(() => {
    web.current?.injectJavaScript(postToPage(pageThemeMessage(theme)));
  }, [theme]);
  // Back on the network: load the page again.
  const wasOnline = useRef(online);
  useEffect(() => {
    if (online && !wasOnline.current) {
      setFailed(false);
      setReloads((n) => n + 1);
    }
    wasOnline.current = online;
  }, [online]);
  // Another page, or the same page shown again: forget the old pick.
  useEffect(() => setProposal(null), [page?.key, page?.choice?.callId]);

  function onMessage(event: WebViewMessageEvent) {
    const message = readBridgeMessage(event.nativeEvent.data);
    if (!message || !page) return;
    switch (message.type) {
      case "fleet:page-hello":
        web.current?.injectJavaScript(postToPage(pageStateMessage(page.pageId)));
        break;
      case "fleet:page-state":
        keepPageState(page.pageId, message.state);
        break;
      case "fleet:page-choice":
        if (live) setProposal({ value: message.value, summary: message.summary });
        break;
      case "fleet:page-reply":
        setReply(message.text);
        break;
    }
  }

  async function choose(action: "send" | "skip") {
    const callId = page?.choice?.callId;
    if (!callId || !api) return;
    setSending(true);
    setError(null);
    try {
      if (action === "send" && proposal) await api.sendPageChoice(session, callId, proposal.value, proposal.summary);
      else await api.skipPageChoice(session, callId);
      // The agent carries on in the conversation, which shows how it went.
      if (router.canGoBack()) router.back();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSending(false);
    }
  }

  async function sendReply() {
    if (!page || !api || !reply?.trim()) return;
    setReplySending(true);
    setError(null);
    try {
      await api.prompt(session, pagePrompt(page, reply));
      setReply(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setReplySending(false);
    }
  }

  const step = useRef((by: number) => {});
  step.current = (by: number) => {
    const next = pages[index + by];
    if (next) setCurrentKey(next.key);
  };
  const swipe = useMemo(() => PanResponder.create({
    onMoveShouldSetPanResponder: (_, g) => Math.abs(g.dx) > 12 && Math.abs(g.dx) > Math.abs(g.dy) * 2,
    onPanResponderRelease: (_, g) => {
      if (Math.abs(g.dx) > 60) step.current(g.dx < 0 ? 1 : -1);
    },
  }), []);

  const where = machineView?.machineName || "your computer";
  return (
    <KeyboardAvoidingView behavior={Platform.OS === "ios" ? "padding" : "height"} style={{ flex: 1, backgroundColor: color.panel, paddingTop: insets.top, paddingBottom: insets.bottom }} testID="page-screen">
      <View {...swipe.panHandlers} style={{ flexDirection: "row", alignItems: "center", gap: 2, paddingHorizontal: 4, paddingVertical: 4, borderBottomWidth: 1, borderColor: color.border }}>
        <Pressable testID="page-back" accessibilityLabel="Back" hitSlop={8} onPress={() => (router.canGoBack() ? router.back() : router.replace("/"))} style={{ width: 44, height: 44, alignItems: "center", justifyContent: "center" }}>
          <Icon name="back" size={22} color={color.text} />
        </Pressable>
        <View style={{ flex: 1, gap: 1 }}>
          <Text weight="semibold" size={15} numberOfLines={1}>{page?.title ?? " "}</Text>
          <Text size={12} tone="muted" numberOfLines={1}>{pages.length ? `${index + 1} of ${pages.length}` : ""}{sessionTitle ? ` · ${sessionTitle}` : ""}</Text>
        </View>
        <Pressable testID="page-menu" accessibilityLabel="More" hitSlop={8} onPress={() => setMenu(true)} style={{ width: 44, height: 44, alignItems: "center", justifyContent: "center" }}>
          <Icon name="more" size={20} color={color.text} />
        </Pressable>
      </View>

      <View style={{ flex: 1, backgroundColor: color.panel }}>
        {page && machineView && online && !failed ? (
          <PageWebView
            ref={web}
            key={`${page.key}:${page.shownAt}:${reloads}`}
            testID="page-webview"
            source={{ uri: pageUrl(machineView.baseUrl, page) }}
            injectedJavaScriptBeforeContentLoaded={shim}
            onMessage={onMessage}
            onError={() => setFailed(true)}
            onHttpError={() => setFailed(true)}
            originWhitelist={["*"]}
            setSupportMultipleWindows={false}
            style={{ flex: 1, backgroundColor: color.panel }}
          />
        ) : page ? (
          <View style={{ flex: 1, alignItems: "center", justifyContent: "center", gap: 10, padding: 32 }}>
            <Text size={17} weight="semibold" style={{ textAlign: "center" }}>This page is on {where}</Text>
            <Text tone="muted" style={{ textAlign: "center" }}>{online ? "It didn't load." : `It opens when ${where} is reachable again.`}</Text>
            {online ? <Button label="Reload" icon="reload" onPress={() => { setFailed(false); setReloads((n) => n + 1); }} /> : null}
          </View>
        ) : (
          <View style={{ flex: 1, alignItems: "center", justifyContent: "center", padding: 32 }}>
            <Text tone="muted">{state ? "This page was closed." : "Loading…"}</Text>
          </View>
        )}
        {/* Edges: swipe in for the next or previous page. Narrow, so the page keeps its own horizontal scrolling. */}
        {pages.length > 1 ? (
          <>
            <View {...swipe.panHandlers} style={{ position: "absolute", left: 0, top: 0, bottom: 0, width: 18 }} />
            <View {...swipe.panHandlers} style={{ position: "absolute", right: 0, top: 0, bottom: 0, width: 18 }} />
          </>
        ) : null}
      </View>

      {error ? <Text tone="error" size={13} style={{ paddingHorizontal: 16, paddingTop: 6 }}>{error}</Text> : null}
      {bar ? (
        <PageChoiceBar
          text={bar.text} strong={bar.strong} after={bar.after} canSend={bar.kind === "proposed"} sending={sending}
          onSend={live ? () => void choose("send") : undefined}
          onSkip={live ? () => void choose("skip") : undefined}
          onReply={live ? () => setReply("") : undefined}
        />
      ) : null}
      {!live ? <PageReplyBar onPress={() => setReply("")} /> : null}

      <Sheet open={menu} onClose={() => setMenu(false)} title={page?.title}>
        <View style={{ gap: 8 }}>
          <Button label="Open in browser" icon="external-link" onPress={() => { setMenu(false); if (page && machineView) void Linking.openURL(pageUrl(machineView.baseUrl, page)); }} testID="page-open-browser" />
          <Button label="Reload" icon="reload" onPress={() => { setMenu(false); setFailed(false); setReloads((n) => n + 1); }} testID="page-reload" />
        </View>
      </Sheet>

      <Sheet open={reply !== null} onClose={() => setReply(null)} title="Reply about this page">
        <View style={{ gap: 10 }}>
          <View style={{ flexDirection: "row", alignItems: "center", gap: 6, alignSelf: "flex-start", paddingHorizontal: 8, height: 26, borderRadius: 6, backgroundColor: color.accentDim }}>
            <Icon name="page" size={13} color={color.accent} />
            <Text size={12.5} weight="medium" tone="accent" numberOfLines={1}>About: {page?.title}</Text>
          </View>
          <Composer value={reply ?? ""} onChangeText={setReply} onSend={() => void sendReply()} sending={replySending} placeholder="Reply about this page" />
        </View>
      </Sheet>
    </KeyboardAvoidingView>
  );
}
