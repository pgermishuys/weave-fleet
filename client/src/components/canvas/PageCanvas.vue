<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import { ExternalLink, RotateCw, TriangleAlert } from "lucide-vue-next";
import { appendDraftText } from "@/composables/use-draft-state";
import { apiUrlOn } from "@/lib/api-client";
import { useMachineTarget } from "@/lib/machine-target";
import { dispatchCommandEvent } from "@/lib/command-events";
import { keepPageState, pageStateMessage, readPageMessage } from "@/lib/page-bridge";
import { pageAddress, type ShownPage } from "@/lib/server-canvas";

/**
 * A page the agent wrote, from Fleet's copy of it (`fleet_page_show`). Every show is a new `shownAt`, which
 * reloads the frame, so the user sees each edit without doing anything.
 *
 * The frame's sandbox matches the one Fleet serves the page with: scripts run, but the page gets an opaque
 * origin, so it can't reach Fleet's cookies or API. Its one way out is a few postMessages to this canvas
 * (lib/page-bridge): text for the composer, which the user then sends, and state that Fleet keeps for the page.
 */
const props = defineProps<{
  sessionId: string;
  page: ShownPage & { title: string };
}>();

const machine = useMachineTarget();

const reloads = ref(0);
const frame = ref<HTMLIFrameElement | null>(null);

const address = computed(() => apiUrlOn(machine.connection, pageAddress(props.page)));
const frameKey = computed(() => `${props.page.pageId}:${props.page.shownAt}:${reloads.value}`);
const fileName = computed(() => props.page.source.split(/[\\/]/).pop() || props.page.entry);

function openOutside(): void {
  window.open(address.value, "_blank", "noopener");
}

function onMessage(event: MessageEvent): void {
  // Only this canvas's own frame, which the sandbox keeps at an opaque origin wherever it navigates.
  const page = frame.value?.contentWindow;
  if (!page || event.source !== page || event.origin !== "null") return;
  const message = readPageMessage(event.data);
  if (!message) return;

  switch (message.type) {
    case "fleet:page-hello":
      page.postMessage(pageStateMessage(props.page.pageId), "*");
      return;
    case "fleet:page-state":
      keepPageState(props.page.pageId, message.state);
      return;
    case "fleet:page-reply":
      appendDraftText(props.sessionId, message.text);
      dispatchCommandEvent("weave:command-focus-prompt", { sessionId: props.sessionId });
      return;
  }
}

onMounted(() => window.addEventListener("message", onMessage));
onBeforeUnmount(() => window.removeEventListener("message", onMessage));
</script>

<template>
  <section class="page-canvas">
    <div class="page-canvas__bar">
      <button
        type="button"
        class="page-canvas__icon"
        title="Reload"
        aria-label="Reload"
        @click="reloads++"
      >
        <RotateCw :size="14" />
      </button>
      <p
        v-if="page.label"
        class="page-canvas__source"
        :title="page.label"
      >
        <span class="page-canvas__file">{{ page.label }}</span>
      </p>
      <p
        v-else
        class="page-canvas__source"
        :title="page.source"
      >
        <span class="page-canvas__file">{{ fileName }}</span>
        <span class="page-canvas__path">{{ page.source }}</span>
      </p>
      <button
        type="button"
        class="page-canvas__icon"
        title="Open in a new tab"
        aria-label="Open in a new tab"
        @click="openOutside"
      >
        <ExternalLink :size="14" />
      </button>
    </div>

    <ul
      v-if="page.warnings.length > 0"
      class="page-canvas__warnings"
      aria-label="Links that won't load"
    >
      <li
        v-for="warning in page.warnings"
        :key="warning"
      >
        <TriangleAlert
          :size="13"
          aria-hidden="true"
        />
        <span>{{ warning }}</span>
      </li>
    </ul>

    <div class="page-canvas__page">
      <iframe
        ref="frame"
        :key="frameKey"
        :src="address"
        :title="page.title"
        class="page-canvas__frame"
        sandbox="allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox allow-modals allow-downloads"
        referrerpolicy="no-referrer"
      />
    </div>
  </section>
</template>

<style scoped>
.page-canvas {
  display: flex;
  flex-direction: column;
  width: 100%;
  height: 100%;
  min-height: 0;
}

.page-canvas__bar {
  display: flex;
  align-items: center;
  gap: 2px;
  height: 40px;
  flex-shrink: 0;
  padding: 0 8px;
  border-bottom: 1px solid var(--border);
}

.page-canvas__icon {
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

.page-canvas__icon:hover {
  background: var(--accent-dim);
  color: var(--text);
}

.page-canvas__source {
  display: flex;
  align-items: baseline;
  gap: 8px;
  flex: 1;
  min-width: 0;
  margin: 0 4px;
  font-size: 12px;
  white-space: nowrap;
}

.page-canvas__file {
  flex-shrink: 0;
  color: var(--text);
  font-weight: 500;
}

.page-canvas__path {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
}

.page-canvas__warnings {
  display: flex;
  flex-direction: column;
  gap: 4px;
  flex-shrink: 0;
  margin: 0;
  padding: 8px 12px;
  border-bottom: 1px solid var(--border);
  list-style: none;
  color: var(--text);
  font-size: 12px;
  background: color-mix(in srgb, var(--status-waiting) 8%, transparent);
}

.page-canvas__warnings li {
  display: flex;
  align-items: flex-start;
  gap: 6px;
}

.page-canvas__warnings svg {
  flex-shrink: 0;
  margin-top: 2px;
  color: var(--status-waiting);
}

.page-canvas__page {
  position: relative;
  flex: 1;
  min-height: 0;
  background: var(--panel-bg);
}

.page-canvas__frame {
  display: block;
  width: 100%;
  height: 100%;
  border: 0;
  /* Pages without a background of their own expect a white one. */
  background: #fff;
  /* As in the browser canvas: keep Fleet's dark scheme off the frame, or a dark page shows its light text on the
     white above. */
  color-scheme: light;
}
</style>
