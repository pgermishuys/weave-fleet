<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from "vue";
import { apiUrl } from "@/lib/api-client";
import { agentFrameUrl } from "@/composables/use-agent-browser";
import { useMachineImage } from "@/composables/use-machine-image";
import { useAgentBrowserStore } from "@/stores/agent-browser";

/**
 * Agent's view: a live picture of the agent's own tab, which runs in Fleet's headless browser, not the user's. A
 * picture, not the page: the user watches, they don't click. The element the agent's latest step acted on gets a
 * ring, and the step a caption, for a few seconds after it happened.
 */
const props = defineProps<{
  sessionId: string;
  tabId: string;
  /** Whether the canvas is on screen; the picture only refreshes while it is. */
  active: boolean;
}>();

/** How often the picture refreshes while it's watched. */
const FRAME_MS = 700;
/** How long a step's ring and caption stay. */
const STEP_MS = 6000;
/** The agent's tab is this size (Fleet's headless browser window). */
const WIDTH = 1280;
const HEIGHT = 800;

const store = useAgentBrowserStore();
const nonce = ref(0);
const now = ref(Date.now());
const stage = ref<HTMLElement | null>(null);
const size = shallowRef({ width: 0, height: 0 });
// The last picture that loaded stays up while the next one comes, so the view doesn't flicker.
const shown = shallowRef<string | null>(null);

const { src, failed } = useMachineImage(() => apiUrl(agentFrameUrl(props.sessionId, props.tabId, nonce.value)));
watch(src, (next) => {
  if (!next) return;
  const image = new Image();
  image.onload = () => {
    shown.value = next;
  };
  image.src = next;
});

const step = computed(() => store.lastStepOn(props.sessionId, props.tabId));
const fresh = computed(() => (step.value ? now.value - Date.parse(step.value.at) < STEP_MS : false));

/** Where the picture sits in the stage (it keeps its shape, at the top), so the ring lands on the element. */
const frame = computed(() => {
  const { width, height } = size.value;
  if (width === 0 || height === 0) return null;
  const scale = Math.min(width / WIDTH, height / HEIGHT);
  // Top-aligned, like a page: the space a tall panel leaves goes below it.
  return { scale, left: (width - WIDTH * scale) / 2, top: 0 };
});

/** The caption and the size tag sit on the picture's bottom edge, not the panel's. */
const onPicture = computed(() => {
  if (!frame.value) return undefined;
  const below = size.value.height - HEIGHT * frame.value.scale;
  return { bottom: `${Math.max(0, below) + 12}px` };
});

const ring = computed(() => {
  const box = step.value?.box;
  if (!fresh.value || !box || !frame.value) return null;
  const { scale, left, top } = frame.value;
  const pad = 4;
  return {
    left: `${left + box.x * scale - pad}px`,
    top: `${top + box.y * scale - pad}px`,
    width: `${box.width * scale + pad * 2}px`,
    height: `${box.height * scale + pad * 2}px`,
  };
});

let timer: number | undefined;
let observer: ResizeObserver | undefined;

function tick(): void {
  now.value = Date.now();
  if (props.active && document.visibilityState === "visible") nonce.value += 1;
}

onMounted(() => {
  timer = window.setInterval(tick, FRAME_MS);
  if (stage.value) {
    observer = new ResizeObserver(([entry]) => {
      if (entry) size.value = { width: entry.contentRect.width, height: entry.contentRect.height };
    });
    observer.observe(stage.value);
  }
});

onBeforeUnmount(() => {
  window.clearInterval(timer);
  observer?.disconnect();
});
</script>

<template>
  <div
    ref="stage"
    class="agent-tab"
    data-testid="agent-tab-view"
  >
    <img
      v-if="shown"
      :src="shown"
      class="agent-tab__picture"
      :width="WIDTH"
      :height="HEIGHT"
      alt="What the agent's tab shows now"
      draggable="false"
    >
    <p
      v-else
      class="agent-tab__empty"
    >
      {{ failed ? "The agent's tab is closed." : "Loading the agent's tab…" }}
    </p>
    <div
      v-if="ring"
      class="agent-tab__ring"
      :style="ring"
      aria-hidden="true"
    />
    <p
      v-if="fresh && step"
      class="agent-tab__caption"
      :class="{ 'agent-tab__caption--failed': !step.ok }"
      :style="onPicture"
      role="status"
    >
      {{ step.summary }}
    </p>
    <span
      class="agent-tab__tag"
      :style="onPicture"
    >agent's tab · {{ WIDTH }}×{{ HEIGHT }}</span>
  </div>
</template>

<style scoped>
.agent-tab {
  position: absolute;
  inset: 0;
  overflow: hidden;
  background: color-mix(in srgb, var(--text) 6%, var(--panel-bg));
  user-select: none;
}

.agent-tab__picture {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: contain;
  object-position: top center;
}

.agent-tab__empty {
  position: absolute;
  inset: 0;
  display: grid;
  place-items: center;
  margin: 0;
  color: var(--muted);
  font-size: 13px;
}

.agent-tab__ring {
  position: absolute;
  border: 2px solid var(--accent);
  border-radius: 6px;
  box-shadow: 0 0 0 4px color-mix(in srgb, var(--accent) 22%, transparent);
  pointer-events: none;
  transition: left 200ms ease-out, top 200ms ease-out, width 200ms ease-out, height 200ms ease-out;
}

.agent-tab__caption {
  position: absolute;
  left: 12px;
  bottom: 12px;
  max-width: calc(100% - 24px);
  margin: 0;
  padding: 6px 10px;
  border-radius: 8px;
  background: rgb(20 20 24 / 0.86);
  color: #f2f2f5;
  font-size: 12px;
  overflow-wrap: anywhere;
}

.agent-tab__caption--failed {
  background: color-mix(in srgb, var(--error) 80%, black);
}

.agent-tab__tag {
  position: absolute;
  right: 12px;
  bottom: 12px;
  padding: 3px 7px;
  border-radius: 6px;
  font-size: 11px;
  color: var(--muted);
  background: color-mix(in srgb, var(--panel-bg) 85%, transparent);
  border: 1px solid var(--border);
}

@container browser-canvas (max-width: 420px) {
  .agent-tab__tag {
    display: none;
  }
}

@media (prefers-reduced-motion: reduce) {
  .agent-tab__ring {
    transition: none;
  }
}
</style>
