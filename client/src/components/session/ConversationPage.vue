<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, shallowRef, useTemplateRef, watch } from "vue";
import { ExternalLink, Maximize2, X } from "lucide-vue-next";
import type { ToolCardPage } from "@/components/session/activity-stream-tool-card";
import { apiUrlOn } from "@/lib/api-client";
import { useMachineTarget } from "@/lib/machine-target";
import { readPageSize } from "@/lib/page-bridge";
import { pageFrameName, pageThemeMessage, usePageTheme } from "@/lib/page-theme";

/**
 * A page the agent showed in the conversation (`fleet_page_show` with placement "conversation"), under its call and
 * above its reply. It sits on the conversation's own background in Fleet's theme, and its frame grows to fit it: Fleet's
 * script in the page reports its height. It can open full size, over the app, or in a browser tab of its own.
 *
 * The sandbox is the page tab's: scripts run, but the page gets an opaque origin, so it can't reach Fleet's cookies or API.
 */
const props = defineProps<{
  page: ToolCardPage;
  /** The call's title, which names the page. */
  title: string;
}>();

// Before the page reports its height. Short, so a page that never reports (it failed to load) leaves little space.
const START_HEIGHT = 120;

const machine = useMachineTarget();
const theme = usePageTheme();
const frame = useTemplateRef<HTMLIFrameElement>("frame");
const fullFrame = useTemplateRef<HTMLIFrameElement>("fullFrame");
const closeButton = useTemplateRef<HTMLButtonElement>("closeButton");

const address = computed(() => apiUrlOn(machine.connection, props.page.path));
// Read once: the page reads its frame's name when it loads, and later changes go by message.
const frameName = pageFrameName("conversation", theme.value);
const height = shallowRef(knownHeights.get(props.page.id) ?? START_HEIGHT);
const expanded = shallowRef(false);
let returnFocusTo: HTMLElement | null = null;

function sendTheme(target: HTMLIFrameElement | null): void {
  target?.contentWindow?.postMessage(pageThemeMessage(theme.value), "*");
}

watch(theme, () => {
  sendTheme(frame.value);
  sendTheme(fullFrame.value);
});

function onMessage(event: MessageEvent): void {
  // Only this page's own frame, which the sandbox keeps at an opaque origin wherever it navigates.
  if (!frame.value?.contentWindow || event.source !== frame.value.contentWindow || event.origin !== "null") return;
  const reported = readPageSize(event.data);
  if (reported === null) return;
  height.value = Math.max(reported, 24);
  knownHeights.set(props.page.id, height.value);
}

function openOutside(): void {
  window.open(address.value, "_blank", "noopener");
}

function closeFullSize(): void {
  expanded.value = false;
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key !== "Escape") return;
  event.preventDefault();
  event.stopPropagation();
  closeFullSize();
}

watch(expanded, async (open) => {
  if (open) {
    returnFocusTo = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    document.addEventListener("keydown", onKeydown, true);
    await nextTick();
    closeButton.value?.focus();
  } else {
    document.removeEventListener("keydown", onKeydown, true);
    returnFocusTo?.focus();
    returnFocusTo = null;
  }
});

onMounted(() => window.addEventListener("message", onMessage));
onBeforeUnmount(() => {
  window.removeEventListener("message", onMessage);
  document.removeEventListener("keydown", onKeydown, true);
});
</script>

<script lang="ts">
// The height each page last reported, so a page drawn again (the session reopened) takes its space at once.
const knownHeights = new Map<string, number>();
</script>

<template>
  <figure
    class="conversation-page"
    data-testid="conversation-page"
  >
    <iframe
      ref="frame"
      :src="address"
      :name="frameName"
      :title="title"
      :style="{ height: `${height}px` }"
      class="conversation-page__frame"
      loading="lazy"
      sandbox="allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox allow-modals allow-downloads"
      referrerpolicy="no-referrer"
      @load="sendTheme(frame)"
    />
    <div class="conversation-page__bar">
      <button
        type="button"
        class="conversation-page__action"
        :title="`Open full size: ${title}`"
        :aria-label="`Open full size: ${title}`"
        data-testid="conversation-page-expand"
        @click="expanded = true"
      >
        <Maximize2 :size="14" />
      </button>
      <button
        type="button"
        class="conversation-page__action"
        title="Open in a new tab"
        aria-label="Open in a new tab"
        @click="openOutside"
      >
        <ExternalLink :size="14" />
      </button>
    </div>

    <Teleport to="body">
      <div
        v-if="expanded"
        class="conversation-page__overlay"
        role="dialog"
        aria-modal="true"
        :aria-label="title"
        data-testid="conversation-page-full"
        @click="closeFullSize"
      >
        <div
          class="conversation-page__sheet"
          @click.stop
        >
          <header class="conversation-page__head">
            <span class="conversation-page__title">{{ title }}</span>
            <button
              ref="closeButton"
              type="button"
              class="conversation-page__action"
              title="Close"
              aria-label="Close"
              @click="closeFullSize"
            >
              <X :size="16" />
            </button>
          </header>
          <iframe
            ref="fullFrame"
            :src="address"
            :name="frameName"
            :title="title"
            class="conversation-page__full"
            sandbox="allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox allow-modals allow-downloads"
            referrerpolicy="no-referrer"
            @load="sendTheme(fullFrame)"
          />
        </div>
      </div>
    </Teleport>
  </figure>
</template>

<style scoped>
.conversation-page {
  position: relative;
  margin: 12px 0 4px;
}

/* Borderless and transparent: the page is drawn on the conversation's own background, as part of the reply. */
.conversation-page__frame {
  display: block;
  width: 100%;
  border: 0;
  background: transparent;
}

.conversation-page__bar {
  position: absolute;
  top: -6px;
  right: -6px;
  display: flex;
  gap: 2px;
  padding: 2px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  box-shadow: var(--menu-shadow);
  opacity: 0;
  transition: opacity var(--transition);
}

.conversation-page:hover .conversation-page__bar,
.conversation-page__bar:focus-within {
  opacity: 1;
}

@media (hover: none) {
  .conversation-page__bar {
    opacity: 1;
  }
}

.conversation-page__action {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 26px;
  padding: 0;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.conversation-page__action:hover {
  background: var(--accent-dim);
  color: var(--text);
}

.conversation-page__action:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.conversation-page__overlay {
  position: fixed;
  inset: 0;
  z-index: 9999;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
  background: rgba(0, 0, 0, 0.6);
}

.conversation-page__sheet {
  display: flex;
  flex-direction: column;
  width: min(1200px, 100%);
  height: min(900px, 100%);
  border: 1px solid var(--border);
  border-radius: var(--radius-panel);
  background: var(--panel-bg);
  box-shadow: var(--menu-shadow);
  overflow: hidden;
}

.conversation-page__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 8px 10px 8px 16px;
  border-bottom: 1px solid var(--border);
}

.conversation-page__title {
  min-width: 0;
  overflow: hidden;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.conversation-page__full {
  flex: 1;
  min-height: 0;
  width: 100%;
  padding: 16px 20px;
  border: 0;
  background: transparent;
}
</style>
