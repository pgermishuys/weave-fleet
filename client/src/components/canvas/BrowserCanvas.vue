<script setup lang="ts">
import { computed, nextTick, onActivated, onBeforeUnmount, onDeactivated, onMounted, ref, watch } from "vue";
import { ArrowLeft, ArrowRight, ExternalLink, RotateCcw, RotateCw, ScrollText, Square } from "lucide-vue-next";
import { apiFetch } from "@/lib/api-client";

/**
 * A page of a web app running on this machine, framed through Fleet's preview
 * proxy. The proxy is its own origin (`{slug}.localhost:{port}`), so the app's
 * paths, cookies and hot reload work as they are, and it reports navigation
 * back here with postMessage. When Fleet runs the app, the strip at the bottom
 * shows its command, status and output.
 */
const props = defineProps<{
  sessionId: string;
  canvasId: string;
  url: string;
  appId?: string;
}>();

interface AppRun {
  id: string;
  command: string;
  status: "starting" | "running" | "exited" | "stopped";
  exitCode: number | null;
  url: string | null;
  ports: number[];
  logs: string[];
}

interface ProxyInfo {
  slug: string;
  port: number;
  target: string;
}

const POLL_MS = 2000;

const frame = ref<HTMLIFrameElement | null>(null);
const logsRef = ref<HTMLPreElement | null>(null);
const target = ref<URL | null>(null);
const proxyOrigin = ref<string | null>(null);
const frameSrc = ref<string>("about:blank");
const address = ref(props.url);
const editing = ref(false);
const error = ref<string | null>(null);
const reportsLocation = ref(false);
const app = ref<AppRun | null>(null);
const showLogs = ref(false);
const busy = ref(false);
let pollTimer: number | undefined;

const sessionPath = computed(() => `/api/sessions/${encodeURIComponent(props.sessionId)}`);
const statusLabel = computed(() => {
  const run = app.value;
  if (!run) return "";
  if (run.status === "exited") return run.exitCode === null ? "exited" : `exited (${run.exitCode})`;
  return run.status;
});
const isLive = computed(() => app.value?.status === "starting" || app.value?.status === "running");
/** Shown instead of the page while there's none: the app hasn't served one yet, or isn't running. */
const waitingText = computed(() => {
  const status = app.value?.status;
  if (status === "exited") return "The app exited before it served a page. Its output is below.";
  if (status === "stopped") return "The app is stopped.";
  return "Waiting for the app to serve a page…";
});
const ports = computed(() => app.value?.ports ?? []);
const currentPort = computed(() => (target.value ? Number(target.value.port || defaultPort(target.value)) : null));

/** `{slug}.localhost` keeps the app's cookies apart from Fleet's; elsewhere, the host Fleet was opened on. */
function proxyHost(slug: string): string {
  const host = window.location.hostname;
  const local = host === "localhost" || host === "127.0.0.1" || host === "[::1]" || host.endsWith(".localhost");
  return local ? `${slug}.localhost` : host;
}

function defaultPort(url: URL): string {
  return url.protocol === "https:" ? "443" : "80";
}

async function show(pageUrl: string): Promise<void> {
  // A starting app has no page yet; the canvas gets one when it answers.
  if (!pageUrl) {
    error.value = null;
    target.value = null;
    frameSrc.value = "about:blank";
    return;
  }

  let page: URL;
  try {
    page = new URL(pageUrl);
  } catch {
    error.value = `${pageUrl} isn't an address.`;
    return;
  }

  error.value = null;
  if (!target.value || target.value.origin !== page.origin || !proxyOrigin.value) {
    try {
      const response = await apiFetch(`${sessionPath.value}/browser/proxy`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ url: page.toString() }),
      });
      if (!response.ok) {
        const body = (await response.json().catch(() => ({}))) as { error?: string };
        throw new Error(body.error ?? `HTTP ${response.status}`);
      }
      const proxy = (await response.json()) as ProxyInfo;
      proxyOrigin.value = `http://${proxyHost(proxy.slug)}:${proxy.port}`;
    } catch (e) {
      error.value = `Fleet couldn't show ${page.origin}: ${e instanceof Error ? e.message : String(e)}`;
      return;
    }
  }

  target.value = page;
  address.value = page.toString();
  reportsLocation.value = false;
  frameSrc.value = `${proxyOrigin.value}${page.pathname}${page.search}${page.hash}`;
}

function onMessage(event: MessageEvent): void {
  if (!proxyOrigin.value || event.origin !== proxyOrigin.value || !target.value) return;
  const data = event.data as { type?: string; href?: string } | null;
  if (data?.type !== "fleet-browser:location" || typeof data.href !== "string") return;

  reportsLocation.value = true;
  if (!editing.value) address.value = target.value.origin + data.href.slice(proxyOrigin.value.length);
}

function navigate(action: "back" | "forward" | "reload"): void {
  const win = frame.value?.contentWindow;
  if (win && proxyOrigin.value && reportsLocation.value) {
    win.postMessage({ type: "fleet-browser:nav", action }, proxyOrigin.value);
    return;
  }
  // No navigation script on this page (not HTML, or blocked): reload by setting the address again.
  if (action === "reload") reloadFrame();
}

function reloadFrame(): void {
  const src = frameSrc.value;
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

function openOutside(): void {
  window.open(address.value, "_blank", "noopener");
}

function pickPort(event: Event): void {
  const port = Number((event.target as HTMLSelectElement).value);
  if (Number.isFinite(port)) void show(`http://localhost:${port}/`);
}

async function loadApp(): Promise<void> {
  if (!props.appId) return;
  try {
    const response = await apiFetch(`${sessionPath.value}/apps/${encodeURIComponent(props.appId)}`);
    if (!response.ok) {
      app.value = null;
      return;
    }
    const next = (await response.json()) as AppRun;
    const previous = app.value;
    app.value = next;

    // Back up after a restart: show the page again once it answers.
    if (previous && previous.status !== "running" && next.status === "running") {
      if (next.url && target.value && new URL(next.url).origin !== target.value.origin) void show(next.url);
      else reloadFrame();
    }
    if (showLogs.value) scrollLogs();
  } catch {
    // Try again on the next poll.
  }
}

async function appAction(action: "restart" | "stop"): Promise<void> {
  if (!props.appId || busy.value) return;
  busy.value = true;
  try {
    await apiFetch(`${sessionPath.value}/apps/${encodeURIComponent(props.appId)}/${action}`, { method: "POST" });
    await loadApp();
  } finally {
    busy.value = false;
  }
}

function toggleLogs(): void {
  showLogs.value = !showLogs.value;
  if (showLogs.value) scrollLogs();
}

function scrollLogs(): void {
  void nextTick(() => {
    const el = logsRef.value;
    if (el) el.scrollTop = el.scrollHeight;
  });
}

function startPolling(): void {
  stopPolling();
  if (!props.appId) return;
  void loadApp();
  pollTimer = window.setInterval(() => void loadApp(), POLL_MS);
}

function stopPolling(): void {
  if (pollTimer !== undefined) window.clearInterval(pollTimer);
  pollTimer = undefined;
}

watch(
  () => props.url,
  (url) => void show(url),
);

onMounted(() => {
  window.addEventListener("message", onMessage);
  void show(props.url);
  startPolling();
});
onActivated(startPolling);
onDeactivated(stopPolling);
onBeforeUnmount(() => {
  window.removeEventListener("message", onMessage);
  stopPolling();
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
      >
        {{ error }}
      </p>
      <p
        v-else-if="!target"
        class="browser-canvas__waiting"
      >
        {{ waitingText }}
      </p>
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
      >{{ app.logs.join("\n") }}</pre>
      <div class="browser-canvas__strip">
        <span
          class="browser-canvas__dot"
          :class="`browser-canvas__dot--${app.status}`"
          aria-hidden="true"
        />
        <code
          class="browser-canvas__command"
          :title="app.command"
        >{{ app.command }}</code>
        <span class="browser-canvas__status">{{ statusLabel }}</span>
        <button
          type="button"
          class="browser-canvas__text-btn"
          :disabled="busy"
          @click="appAction('restart')"
        >
          <RotateCcw :size="13" />
          {{ isLive ? "Restart" : "Start" }}
        </button>
        <button
          v-if="isLive"
          type="button"
          class="browser-canvas__text-btn"
          :disabled="busy"
          @click="appAction('stop')"
        >
          <Square :size="12" />
          Stop
        </button>
        <button
          type="button"
          class="browser-canvas__text-btn"
          :class="{ 'browser-canvas__text-btn--on': showLogs }"
          :aria-pressed="showLogs"
          @click="toggleLogs"
        >
          <ScrollText :size="13" />
          Output
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
  background: #fff;
}

.browser-canvas__frame {
  width: 100%;
  height: 100%;
  border: 0;
  display: block;
}

.browser-canvas__error {
  margin: 0;
  padding: 16px;
  color: var(--error);
  font-size: 12.5px;
  background: var(--panel-bg);
  height: 100%;
}

.browser-canvas__waiting {
  margin: 0;
  padding: 16px;
  color: var(--muted);
  font-size: 12.5px;
  background: var(--panel-bg);
  height: 100%;
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
