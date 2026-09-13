<script setup lang="ts">
import { computed, nextTick, onActivated, onBeforeUnmount, onDeactivated, onMounted, ref, watch } from "vue";
import { ArrowLeft, ArrowRight, ExternalLink, Play, RotateCcw, RotateCw, ScrollText, Square } from "lucide-vue-next";
import { apiFetch } from "@/lib/api-client";
import { onReconnect } from "@/composables/use-weave-socket";
import { appAddress, navMessage, readBridgeMessage, type NavAction, type PreviewHmr } from "@/lib/preview-bridge";
import { currentRunLines, useAppRunsStore } from "@/stores/app-runs";
import { serverCanvasTabId, useCanvasesStore } from "@/stores/canvases";

/**
 * A page of a web app running on Fleet's machine, framed through Fleet's preview gateway. The preview is its own
 * origin, so the app's paths, cookies and hot reload work as they are, and the preview bridge reports the page
 * back here. When Fleet runs the app, `app.updated` events drive its status: the page while it runs, a panel
 * while it starts or after it stopped, and a strip with its command, controls and output.
 */
const props = defineProps<{
  sessionId: string;
  canvasId: string;
  /** The page; empty for a tab the user started from the + menu, which shows the app's page. */
  url: string;
  appId?: string;
}>();

interface ProxyInfo {
  /** Where this browser loads the preview; Fleet picks it from the host the browser used to reach it. */
  origin: string;
}

/** How often output is fetched while it's on screen and the app runs. Output isn't pushed. */
const OUTPUT_POLL_MS = 1000;
const TAIL_LINES = 8;

const appRuns = useAppRunsStore();
const canvases = useCanvasesStore();

const frame = ref<HTMLIFrameElement | null>(null);
const logsRef = ref<HTMLPreElement | null>(null);
const target = ref<URL | null>(null);
const proxyOrigin = ref<string | null>(null);
const frameSrc = ref("about:blank");
const address = ref(props.url);
const editing = ref(false);
const error = ref<string | null>(null);
const showLogs = ref(false);
const busy = ref(false);
const actionError = ref<string | null>(null);
const active = ref(true);
/** From the page's hello; none until a page with the bridge has loaded. */
const hmr = ref<PreviewHmr | null>(null);

// A hello that nobody here asked for, for the page that was already shown, is the page reloading itself.
let expectingLoad = true;
let lastHref: string | null = null;
let outputTimer: number | undefined;
let actionErrorTimer: number | undefined;

const tabId = computed(() => serverCanvasTabId(props.canvasId));
const sessionPath = computed(() => `/api/sessions/${encodeURIComponent(props.sessionId)}`);
const app = computed(() => (props.appId ? appRuns.byId[props.appId] ?? null : null));
const outputLines = computed(() => (props.appId ? appRuns.outputById[props.appId]?.lines ?? [] : []));
/** The panel's last lines are this run's: an earlier run's crash doesn't belong under "Starting…". */
const tail = computed(() => currentRunLines(outputLines.value).slice(-TAIL_LINES));
const pageUrl = computed(() => props.url || app.value?.url || "");
const status = computed(() => app.value?.status ?? null);
const isLive = computed(() => status.value === "starting" || status.value === "running" || status.value === "build-failed");

const statusLabel = computed(() => {
  const run = app.value;
  if (!run) return "";
  if (run.status === "exited") return run.exitCode === null ? "exited" : `exited (${run.exitCode})`;
  if (run.status === "build-failed") return "build failed";
  return run.status;
});

/** What the page area shows instead of the page: the app isn't serving one (yet), or there's none to frame. */
const panel = computed<"starting" | "waiting" | "exited" | "stopped" | null>(() => {
  if (status.value === "starting") return "starting";
  if (status.value === "exited") return "exited";
  if (status.value === "stopped") return "stopped";
  return target.value ? null : "waiting";
});

const panelTitle = computed(() => {
  const run = app.value;
  switch (panel.value) {
    case "starting":
      return "Starting…";
    case "exited":
      return run?.exitCode === null || run?.exitCode === undefined ? "The app exited" : `The app exited with code ${run.exitCode}`;
    case "stopped":
      return "The app is stopped";
    default:
      return props.appId ? "Waiting for the app to serve a page…" : "Loading…";
  }
});

/** Output shows in the panel while an app starts or after it exited, and in the drawer when it's open. */
const outputOnScreen = computed(() => showLogs.value || panel.value === "starting" || panel.value === "exited");
const ports = computed(() => app.value?.ports ?? []);
const currentPort = computed(() => (target.value ? Number(target.value.port || defaultPort(target.value)) : null));

function defaultPort(url: URL): string {
  return url.protocol === "https:" ? "443" : "80";
}

function pulse(): void {
  canvases.markUpdated(tabId.value);
}

async function show(page: string): Promise<void> {
  // A starting app has no page yet; the canvas gets one when it answers.
  if (!page) {
    error.value = null;
    target.value = null;
    frameSrc.value = "about:blank";
    return;
  }

  let parsed: URL;
  try {
    parsed = new URL(page);
  } catch {
    error.value = `${page} isn't an address.`;
    return;
  }

  error.value = null;
  if (!target.value || target.value.origin !== parsed.origin || !proxyOrigin.value) {
    try {
      const response = await apiFetch(`${sessionPath.value}/browser/proxy`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ url: parsed.toString() }),
      });
      if (!response.ok) {
        const body = (await response.json().catch(() => ({}))) as { error?: string };
        throw new Error(body.error ?? `HTTP ${response.status}`);
      }
      proxyOrigin.value = ((await response.json()) as ProxyInfo).origin;
    } catch (e) {
      error.value = `Fleet couldn't show ${parsed.origin}: ${e instanceof Error ? e.message : String(e)}`;
      return;
    }
  }

  target.value = parsed;
  if (!editing.value) address.value = parsed.toString();
  expectingLoad = true;
  hmr.value = null;
  frameSrc.value = `${proxyOrigin.value}${parsed.pathname}${parsed.search}${parsed.hash}`;
}

function onMessage(event: MessageEvent): void {
  if (!proxyOrigin.value || event.origin !== proxyOrigin.value || !target.value) return;
  if (frame.value && event.source !== frame.value.contentWindow) return;
  const message = readBridgeMessage(event.data);
  if (!message) return;

  switch (message.type) {
    case "hello":
      hmr.value = message.hmr;
      if (!expectingLoad && message.href === lastHref) pulse();
      expectingLoad = false;
      report(message.href);
      return;
    case "location":
      report(message.href);
      return;
    case "update":
      pulse();
      return;
  }
}

function report(href: string): void {
  lastHref = href;
  if (!editing.value && proxyOrigin.value && target.value) address.value = appAddress(href, proxyOrigin.value, target.value.origin);
}

function navigate(action: NavAction): void {
  const win = frame.value?.contentWindow;
  // Back and forward land on another address, which never counts as the page reloading itself.
  if (action === "reload") expectingLoad = true;
  if (win && proxyOrigin.value && hmr.value !== null) {
    win.postMessage(navMessage(action), proxyOrigin.value);
    return;
  }
  // No bridge on this page (not HTML, or it didn't load): reload by setting the address again.
  if (action === "reload") reloadFrame();
}

function reloadFrame(): void {
  const src = frameSrc.value;
  expectingLoad = true;
  frameSrc.value = "about:blank";
  void nextTick(() => {
    frameSrc.value = src;
  });
}

function submitAddress(): void {
  editing.value = false;
  let value = address.value.trim();
  if (/^\d+$/.test(value)) value = `http://localhost:${value}/`;
  else if (!/^https?:\/\//i.test(value)) value = `http://${value}`;
  void show(value);
}

/** Opens the page the tab shows through its preview: the app's own `localhost` address is only right on Fleet's machine. */
function openOutside(): void {
  let page: URL | null = null;
  try {
    page = new URL(address.value);
  } catch {
    // Not an address (still being typed): open the preview's start.
  }
  const url = proxyOrigin.value
    ? `${proxyOrigin.value}${page ? page.pathname + page.search + page.hash : "/"}`
    : address.value;
  window.open(url, "_blank", "noopener");
}

function pickPort(event: Event): void {
  const port = Number((event.target as HTMLSelectElement).value);
  if (Number.isFinite(port)) void show(`http://localhost:${port}/`);
}

async function loadApp(): Promise<void> {
  if (!props.appId) return;
  try {
    await appRuns.load(props.sessionId, props.appId);
  } catch {
    // The next event or reconnect brings it.
  }
}

async function appAction(action: "restart" | "stop"): Promise<void> {
  if (!props.appId || busy.value) return;
  busy.value = true;
  setActionError(null);
  try {
    setActionError(await appRuns.act(props.sessionId, props.appId, action));
  } finally {
    busy.value = false;
  }
}

function setActionError(message: string | null): void {
  window.clearTimeout(actionErrorTimer);
  actionError.value = message;
  if (message) actionErrorTimer = window.setTimeout(() => (actionError.value = null), 10_000);
}

function toggleLogs(): void {
  showLogs.value = !showLogs.value;
  if (showLogs.value) scrollLogs(true);
}

function scrollLogs(force = false): void {
  const el = logsRef.value;
  const atBottom = !el || el.scrollHeight - el.scrollTop - el.clientHeight < 24;
  void nextTick(() => {
    const after = logsRef.value;
    if (after && (force || atBottom)) after.scrollTop = after.scrollHeight;
  });
}

function fetchOutput(): void {
  if (props.appId) void appRuns.fetchOutput(props.sessionId, props.appId).catch(() => {});
}

// Output only while someone can see it: every second while the app runs, once when it doesn't.
watch(
  [active, outputOnScreen, isLive, () => props.appId],
  ([isActive, onScreen, live]) => {
    window.clearInterval(outputTimer);
    outputTimer = undefined;
    if (!isActive || !onScreen || !props.appId) return;
    fetchOutput();
    if (live) outputTimer = window.setInterval(fetchOutput, OUTPUT_POLL_MS);
  },
  { immediate: true },
);

watch(outputLines, () => {
  if (showLogs.value) scrollLogs();
});

watch(pageUrl, (page) => void show(page));

// The app's status, from its events.
watch(
  () => [status.value, app.value?.url] as const,
  ([next, url], previous) => {
    const [before] = previous ?? [null, null];
    // Came back after a restart: show its page again, and pulse like any reload. A tab with a page of its own
    // follows the app when it moved to another port; one without follows the app's page anyway (pageUrl).
    if (next === "running" && before !== null && before !== "running" && before !== "build-failed") {
      expectingLoad = true;
      if (props.url && url && target.value && new URL(url).origin !== target.value.origin) void show(url);
      if (lastHref !== null) pulse();
    }
    // A failed build is in the output, so open it once.
    if (next === "build-failed" && before !== "build-failed") {
      showLogs.value = true;
      scrollLogs(true);
    }
  },
);

onMounted(() => {
  window.addEventListener("message", onMessage);
  void show(pageUrl.value);
  void loadApp();
});

// Kept alive between tab switches; the frame loads again when the tab comes back.
onActivated(() => {
  active.value = true;
  expectingLoad = true;
  void loadApp();
});
onDeactivated(() => {
  active.value = false;
});

const stopReconnect = onReconnect(() => void loadApp());

onBeforeUnmount(() => {
  window.removeEventListener("message", onMessage);
  window.clearInterval(outputTimer);
  window.clearTimeout(actionErrorTimer);
  stopReconnect();
});
</script>

<template>
  <section class="browser-canvas">
    <div class="browser-canvas__bar">
      <button
        type="button"
        class="browser-canvas__icon"
        title="Back"
        aria-label="Back"
        @click="navigate('back')"
      >
        <ArrowLeft :size="15" />
      </button>
      <button
        type="button"
        class="browser-canvas__icon"
        title="Forward"
        aria-label="Forward"
        @click="navigate('forward')"
      >
        <ArrowRight :size="15" />
      </button>
      <button
        type="button"
        class="browser-canvas__icon"
        title="Reload"
        aria-label="Reload"
        @click="navigate('reload')"
      >
        <RotateCw :size="14" />
      </button>
      <form
        class="browser-canvas__address"
        @submit.prevent="submitAddress"
      >
        <input
          v-model="address"
          type="text"
          spellcheck="false"
          aria-label="Address"
          :placeholder="appId ? 'Waiting for the app…' : 'localhost:5173'"
          @focus="editing = true"
          @blur="editing = false"
        >
      </form>
      <select
        v-if="ports.length > 1"
        class="browser-canvas__ports"
        aria-label="Port"
        :value="currentPort ?? undefined"
        @change="pickPort"
      >
        <option
          v-for="port in ports"
          :key="port"
          :value="port"
        >
          :{{ port }}
        </option>
      </select>
      <button
        type="button"
        class="browser-canvas__icon"
        title="Open in a new tab"
        aria-label="Open in a new tab"
        @click="openOutside"
      >
        <ExternalLink :size="14" />
      </button>
    </div>

    <div class="browser-canvas__page">
      <p
        v-if="error"
        class="browser-canvas__error"
        role="alert"
      >
        {{ error }}
      </p>
      <div
        v-else-if="panel"
        class="browser-canvas__panel"
        :class="`browser-canvas__panel--${panel}`"
        data-testid="browser-canvas-panel"
      >
        <div class="browser-canvas__panel-head">
          <span
            class="browser-canvas__dot"
            :class="`browser-canvas__dot--${status ?? 'starting'}`"
            aria-hidden="true"
          />
          <p class="browser-canvas__panel-title">
            {{ panelTitle }}
          </p>
        </div>
        <code
          v-if="app"
          class="browser-canvas__panel-command"
        >{{ app.command }}</code>
        <button
          v-if="panel === 'exited' || panel === 'stopped'"
          type="button"
          class="browser-canvas__start"
          :disabled="busy"
          @click="appAction('restart')"
        >
          <Play :size="13" />
          Start
        </button>
        <pre
          v-if="(panel === 'starting' || panel === 'exited') && tail.length > 0"
          class="browser-canvas__tail"
          aria-label="Recent output"
        >{{ tail.join("\n") }}</pre>
      </div>
      <iframe
        v-else
        ref="frame"
        :src="frameSrc"
        title="App preview"
        class="browser-canvas__frame"
        sandbox="allow-scripts allow-same-origin allow-forms allow-popups allow-modals allow-downloads"
      />
    </div>

    <div
      v-if="app"
      class="browser-canvas__app"
    >
      <pre
        v-if="showLogs"
        ref="logsRef"
        class="browser-canvas__logs"
        aria-label="Output"
      >{{ outputLines.length > 0 ? outputLines.join("\n") : "No output yet." }}</pre>
      <p
        v-if="actionError"
        class="browser-canvas__action-error"
        role="alert"
      >
        {{ actionError }}
      </p>
      <div
        class="browser-canvas__strip"
        data-testid="browser-canvas-strip"
      >
        <span
          class="browser-canvas__dot"
          :class="`browser-canvas__dot--${app.status}`"
          aria-hidden="true"
        />
        <code
          class="browser-canvas__command"
          :title="app.command"
        >{{ app.command }}</code>
        <span
          class="browser-canvas__status"
          :class="{ 'browser-canvas__status--failed': app.status === 'build-failed' }"
        >{{ statusLabel }}</span>
        <button
          type="button"
          class="browser-canvas__text-btn"
          :disabled="busy"
          :title="isLive ? 'Restart' : 'Start'"
          :aria-label="isLive ? 'Restart' : 'Start'"
          @click="appAction('restart')"
        >
          <RotateCcw
            v-if="isLive"
            :size="13"
          />
          <Play
            v-else
            :size="13"
          />
          <span class="browser-canvas__btn-label">{{ isLive ? "Restart" : "Start" }}</span>
        </button>
        <button
          v-if="isLive"
          type="button"
          class="browser-canvas__text-btn"
          :disabled="busy"
          title="Stop"
          aria-label="Stop"
          @click="appAction('stop')"
        >
          <Square :size="12" />
          <span class="browser-canvas__btn-label">Stop</span>
        </button>
        <button
          type="button"
          class="browser-canvas__text-btn"
          :class="{ 'browser-canvas__text-btn--on': showLogs }"
          :aria-pressed="showLogs"
          title="Output"
          aria-label="Output"
          @click="toggleLogs"
        >
          <ScrollText :size="13" />
          <span class="browser-canvas__btn-label">Output</span>
        </button>
      </div>
    </div>
  </section>
</template>

<style scoped>
.browser-canvas {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  container: browser-canvas / inline-size;
}

.browser-canvas__bar {
  display: flex;
  align-items: center;
  gap: 2px;
  height: 40px;
  flex-shrink: 0;
  padding: 0 8px;
  border-bottom: 1px solid var(--border);
}

.browser-canvas__icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  flex-shrink: 0;
  border-radius: var(--radius-btn);
  color: var(--muted);
  transition: background var(--transition), color var(--transition);
}

.browser-canvas__icon:hover {
  background: var(--accent-dim);
  color: var(--text);
}

.browser-canvas__address {
  flex: 1;
  min-width: 0;
  margin: 0 4px;
}

.browser-canvas__address input {
  width: 100%;
  height: 28px;
  padding: 0 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: color-mix(in srgb, var(--text) 4%, transparent);
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 12px;
  outline: none;
}

.browser-canvas__address input::placeholder {
  color: var(--muted);
}

.browser-canvas__address input:focus {
  border-color: var(--accent);
}

.browser-canvas__ports {
  height: 28px;
  margin-right: 2px;
  padding: 0 6px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 12px;
}

.browser-canvas__page {
  position: relative;
  flex: 1;
  min-height: 0;
  background: var(--panel-bg);
}

.browser-canvas__frame {
  width: 100%;
  height: 100%;
  border: 0;
  display: block;
  /* Pages without a background of their own expect a white one. */
  background: #fff;
}

.browser-canvas__error {
  margin: 0;
  padding: 16px;
  color: var(--error);
  font-size: 12.5px;
}

.browser-canvas__panel {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 10px;
  max-width: 560px;
  padding: 28px 20px;
}

.browser-canvas__panel-head {
  display: flex;
  align-items: center;
  gap: 9px;
}

.browser-canvas__panel-title {
  margin: 0;
  color: var(--text);
  font-size: 13px;
  font-weight: 500;
}

.browser-canvas__panel-command {
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 12px;
}

.browser-canvas__start {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 28px;
  padding: 0 12px;
  border-radius: var(--radius-btn);
  background: var(--accent);
  color: var(--primary-foreground);
  font-size: 12.5px;
  font-weight: 500;
  transition: opacity var(--transition);
}

.browser-canvas__start:hover:not(:disabled) {
  opacity: 0.88;
}

.browser-canvas__start:disabled {
  opacity: 0.5;
}

.browser-canvas__tail {
  align-self: stretch;
  margin: 4px 0 0;
  padding: 8px 10px;
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: color-mix(in srgb, var(--text) 3%, transparent);
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.browser-canvas__app {
  flex-shrink: 0;
  border-top: 1px solid var(--border);
}

.browser-canvas__logs {
  max-height: 220px;
  margin: 0;
  padding: 8px 12px;
  overflow: auto;
  border-bottom: 1px solid var(--border);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
  color: var(--text);
  background: color-mix(in srgb, var(--text) 3%, transparent);
}

.browser-canvas__action-error {
  margin: 0;
  padding: 6px 12px;
  border-bottom: 1px solid var(--border);
  color: var(--error);
  font-size: 12px;
}

.browser-canvas__strip {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
  height: 36px;
  padding: 0 8px 0 12px;
  font-size: 12px;
}

.browser-canvas__dot {
  width: 7px;
  height: 7px;
  flex-shrink: 0;
  border-radius: 50%;
  background: var(--muted);
}

.browser-canvas__dot--running {
  background: var(--running);
}

.browser-canvas__dot--starting {
  background: var(--idle);
  animation: browser-canvas-pulse var(--transition-pulse) ease-in-out infinite;
}

.browser-canvas__dot--build-failed,
.browser-canvas__dot--exited {
  background: var(--error);
}

.browser-canvas__command {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  color: var(--text);
}

.browser-canvas__status {
  flex-shrink: 0;
  color: var(--muted);
}

.browser-canvas__status--failed {
  color: var(--error);
}

.browser-canvas__text-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  flex-shrink: 0;
  height: 26px;
  padding: 0 8px;
  border-radius: var(--radius-btn);
  color: var(--muted);
  transition: background var(--transition), color var(--transition);
}

.browser-canvas__text-btn:hover:not(:disabled),
.browser-canvas__text-btn--on {
  background: var(--accent-dim);
  color: var(--text);
}

.browser-canvas__text-btn:disabled {
  opacity: 0.5;
}

/* A narrow panel keeps the command readable: the buttons lose their labels, then the status its text. */
@container browser-canvas (max-width: 440px) {
  .browser-canvas__btn-label {
    display: none;
  }

  .browser-canvas__text-btn {
    width: 26px;
    padding: 0;
    justify-content: center;
  }

  .browser-canvas__strip {
    gap: 6px;
  }
}

@container browser-canvas (max-width: 300px) {
  .browser-canvas__status {
    display: none;
  }
}

@keyframes browser-canvas-pulse {
  50% {
    opacity: 0.35;
  }
}

@media (prefers-reduced-motion: reduce) {
  .browser-canvas__dot--starting {
    animation: none;
  }
}
</style>
