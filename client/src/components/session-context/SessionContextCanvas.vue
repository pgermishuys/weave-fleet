<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from "vue";
import { storeToRefs } from "pinia";
import { useRouter } from "@tanstack/vue-router";
import { GitBranch, LoaderCircle, Plus, RefreshCw, Zap } from "lucide-vue-next";
import SmartLinkCard from "@/components/session-context/SmartLinkCard.vue";
import SmartLinkRow from "@/components/session-context/SmartLinkRow.vue";
import { apiFetch } from "@/lib/api-client";
import { formatCheckedAgo, isPullRequest, type SmartLink } from "@/lib/smart-links";
import { useSessionsStore } from "@/stores/sessions";
import { useSmartLinksStore } from "@/stores/smart-links";

const props = defineProps<{
  sessionId: string;
}>();

interface OriginRecord {
  sourceType: string;
  title?: string | null;
  summary?: string | null;
  actionId: string;
  createdAt: string;
}

const router = useRouter();
const store = useSmartLinksStore();
const sessionsStore = useSessionsStore();
const { focusRequest } = storeToRefs(store);

const rootRef = ref<HTMLElement | null>(null);
const originRecords = shallowRef<OriginRecord[]>([]);

watch(
  () => props.sessionId,
  async (sessionId, _previous, onCleanup) => {
    originRecords.value = [];
    if (!sessionId) return;
    void store.ensureLoaded(sessionId);

    const controller = new AbortController();
    onCleanup(() => controller.abort());
    try {
      const response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}/origin`, { signal: controller.signal });
      if (response.ok) originRecords.value = (await response.json()) as OriginRecord[];
    } catch {
      // The origin record only adds a detail line; the tab works without it.
    }
  },
  { immediate: true },
);

const session = computed(() => sessionsStore.sessions.find((s) => s.session.id === props.sessionId) ?? null);
const links = computed(() => store.visibleLinks(props.sessionId));
const byRelationship = (relationship: SmartLink["relationship"]) =>
  links.value.filter((link) => link.relationship === relationship);

const originLink = computed(() => byRelationship("origin")[0] ?? null);
const ownLinks = computed(() => byRelationship("own"));
const pinnedLinks = computed(() => byRelationship("pinned"));
const mentionedLinks = computed(() => byRelationship("mentioned"));

const automationOrigin = computed(() => {
  const origin = session.value?.origin;
  if (origin?.sourceType !== "automation") return null;
  const record = originRecords.value.find((r) => r.sourceType === "automation" && r.actionId === "start-session");
  return { name: origin.title ?? "Automation", summary: record?.summary ?? null, createdAt: record?.createdAt ?? null };
});

const notConnected = computed(() => links.value.some((link) => link.enrichmentStatus === "not_connected"));
const hasLinks = computed(() => links.value.length > 0);
const isEmpty = computed(() => !hasLinks.value && !automationOrigin.value);

const workspace = computed(() => {
  const s = session.value;
  if (!s) return null;
  const rows: { label: string; value: string }[] = [];
  if (s.branch) rows.push({ label: "Branch", value: s.branch });
  if (s.isolationStrategy && s.isolationStrategy !== "existing") rows.push({ label: "Isolation", value: s.isolationStrategy });
  if (s.workspaceDirectory) rows.push({ label: "Directory", value: s.workspaceDirectory });
  return rows.length > 0 ? rows : null;
});

// ── Freshness ────────────────────────────────────────────────────────────────

const now = ref(Date.now());
let clock: ReturnType<typeof setInterval> | undefined;
onMounted(() => {
  clock = setInterval(() => {
    now.value = Date.now();
  }, 15_000);
});
onBeforeUnmount(() => clearInterval(clock));

const checkedAgo = computed(() => formatCheckedAgo(store.lastCheckedAt(props.sessionId), now.value));
const refreshing = ref(false);

async function refresh(): Promise<void> {
  refreshing.value = true;
  await store.refresh(props.sessionId);
  // Results arrive as pushed updates; keep the spinner long enough to read as an action.
  setTimeout(() => {
    refreshing.value = false;
    now.value = Date.now();
  }, 1200);
}

// ── Attach ───────────────────────────────────────────────────────────────────

const attaching = ref(false);
const attachUrl = ref("");
const attachError = ref<string | null>(null);
const attachBusy = ref(false);
const attachInput = ref<HTMLInputElement | null>(null);

async function startAttach(): Promise<void> {
  attaching.value = true;
  attachError.value = null;
  await nextTick();
  attachInput.value?.focus();
}

async function submitAttach(): Promise<void> {
  const url = attachUrl.value.trim();
  if (!url || attachBusy.value) return;
  attachBusy.value = true;
  attachError.value = await store.addLink(props.sessionId, url);
  attachBusy.value = false;
  if (!attachError.value) {
    attachUrl.value = "";
    attaching.value = false;
  }
}

function cancelAttach(): void {
  attaching.value = false;
  attachUrl.value = "";
  attachError.value = null;
}

function connectGitHub(): void {
  void router.navigate({ to: "/settings/plugins/$pluginId", params: { pluginId: "github" } });
}

// ── Focus from header chips ──────────────────────────────────────────────────

watch(
  focusRequest,
  async (request) => {
    if (!request || request.sessionId !== props.sessionId) return;
    await nextTick();
    const selector = request.target === "origin" ? "[data-context-origin]" : `[data-link-id="${CSS.escape(request.target)}"]`;
    const target = rootRef.value?.querySelector<HTMLElement>(selector)
      ?? (request.target === "origin" ? rootRef.value?.querySelector<HTMLElement>('[data-link-relationship="origin"]') : null);
    if (!target) return;
    target.scrollIntoView?.({ block: "nearest", behavior: "smooth" });
    target.classList.remove("context-flash");
    void target.offsetWidth;
    target.classList.add("context-flash");
  },
  { immediate: true },
);
</script>

<template>
  <div
    ref="rootRef"
    class="context-canvas"
  >
    <p
      v-if="notConnected"
      class="context-notice"
    >
      Connect GitHub to see status, checks and reviews.
      <button
        type="button"
        class="context-link-btn"
        @click="connectGitHub"
      >
        Connect GitHub
      </button>
    </p>

    <section
      v-if="originLink || automationOrigin"
      class="context-section"
      aria-labelledby="context-started-from"
    >
      <h3
        id="context-started-from"
        class="context-section__label"
      >
        Started from
      </h3>
      <div
        v-if="automationOrigin"
        class="context-origin"
        data-context-origin
      >
        <Zap
          :size="14"
          class="context-origin__icon"
          aria-hidden="true"
        />
        <div class="context-origin__main">
          <span class="context-origin__title">{{ automationOrigin.name }}</span>
          <span class="context-origin__detail">
            Automation<template v-if="automationOrigin.summary"> · {{ automationOrigin.summary }}</template><template v-if="automationOrigin.createdAt && formatCheckedAgo(automationOrigin.createdAt, now)"> · {{ formatCheckedAgo(automationOrigin.createdAt, now) }}</template>
          </span>
        </div>
      </div>
      <div
        v-else-if="originLink"
        data-link-relationship="origin"
        data-context-origin
      >
        <SmartLinkCard
          v-if="isPullRequest(originLink)"
          :link="originLink"
        />
        <SmartLinkRow
          v-else
          :link="originLink"
          note="Session started here"
        />
      </div>
    </section>

    <section
      v-if="ownLinks.length > 0"
      class="context-section"
      aria-labelledby="context-own"
    >
      <h3
        id="context-own"
        class="context-section__label"
      >
        {{ ownLinks.length === 1 ? "This session's pull request" : "This session's pull requests" }}
      </h3>
      <SmartLinkCard
        v-for="link in ownLinks"
        :key="link.id"
        :link="link"
      />
    </section>

    <section
      v-if="pinnedLinks.length > 0"
      class="context-section"
      aria-labelledby="context-pinned"
    >
      <h3
        id="context-pinned"
        class="context-section__label"
      >
        Pinned
      </h3>
      <template
        v-for="link in pinnedLinks"
        :key="link.id"
      >
        <SmartLinkCard
          v-if="isPullRequest(link)"
          :link="link"
        />
        <SmartLinkRow
          v-else
          :link="link"
        />
      </template>
    </section>

    <section
      v-if="mentionedLinks.length > 0"
      class="context-section"
      aria-labelledby="context-mentioned"
    >
      <h3
        id="context-mentioned"
        class="context-section__label"
      >
        Mentioned in conversation
      </h3>
      <SmartLinkRow
        v-for="link in mentionedLinks"
        :key="link.id"
        :link="link"
      />
    </section>

    <p
      v-if="isEmpty"
      class="context-empty"
    >
      Nothing attached yet. Pull requests and issues mentioned in the conversation, and pull requests this session opens, show up here.
    </p>

    <div class="context-attach">
      <form
        v-if="attaching"
        class="context-attach__form"
        @submit.prevent="submitAttach"
      >
        <label
          for="context-attach-url"
          class="context-attach__label"
        >Attach a pull request or issue</label>
        <input
          id="context-attach-url"
          ref="attachInput"
          v-model="attachUrl"
          class="context-attach__input"
          type="url"
          placeholder="https://github.com/owner/repo/pull/123"
          autocomplete="off"
          @keydown.esc="cancelAttach"
        >
        <div class="context-attach__actions">
          <button
            type="submit"
            class="context-attach__submit"
            :disabled="attachBusy || !attachUrl.trim()"
          >
            Attach
          </button>
          <button
            type="button"
            class="context-link-btn"
            @click="cancelAttach"
          >
            Cancel
          </button>
        </div>
        <p
          v-if="attachError"
          class="context-attach__error"
          role="alert"
        >
          {{ attachError }}
        </p>
      </form>
      <button
        v-else
        type="button"
        class="context-link-btn context-attach__start"
        @click="startAttach"
      >
        <Plus
          :size="13"
          aria-hidden="true"
        />
        Attach a pull request or issue
      </button>
    </div>

    <section
      v-if="workspace"
      class="context-section"
      aria-labelledby="context-workspace"
    >
      <h3
        id="context-workspace"
        class="context-section__label"
      >
        Workspace
      </h3>
      <dl class="context-workspace">
        <template
          v-for="row in workspace"
          :key="row.label"
        >
          <dt>{{ row.label }}</dt>
          <dd :title="row.value">
            <GitBranch
              v-if="row.label === 'Branch'"
              :size="12"
              aria-hidden="true"
            />{{ row.value }}
          </dd>
        </template>
      </dl>
    </section>

    <footer
      v-if="hasLinks"
      class="context-footer"
    >
      <span>{{ checkedAgo ? `GitHub checked ${checkedAgo}` : "Waiting for GitHub" }}</span>
      <button
        type="button"
        class="context-link-btn"
        :disabled="refreshing"
        @click="refresh"
      >
        <LoaderCircle
          v-if="refreshing"
          :size="12"
          class="context-spin"
          aria-hidden="true"
        />
        <RefreshCw
          v-else
          :size="12"
          aria-hidden="true"
        />
        Refresh
      </button>
    </footer>
  </div>
</template>

<style scoped>
.context-canvas {
  display: flex;
  flex: 1;
  min-height: 0;
  flex-direction: column;
  gap: 18px;
  padding: 12px 14px 14px;
  overflow-y: auto;
}

.context-section {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.context-section__label {
  margin: 0;
  color: var(--muted);
  font-size: 10.5px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
}

.context-notice {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 6px 10px;
  margin: 0;
  padding: 9px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: color-mix(in srgb, var(--status-waiting) 8%, transparent);
  color: var(--text);
  font-size: 12.5px;
}

.context-origin {
  display: grid;
  grid-template-columns: 16px minmax(0, 1fr);
  gap: 8px;
  margin-inline: -6px;
  padding: 6px;
  border-radius: var(--radius-btn);
}

.context-origin__icon {
  margin-top: 2px;
  color: var(--status-waiting);
}

.context-origin__main {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 2px;
}

.context-origin__title {
  font-size: 13px;
  font-weight: 500;
}

.context-origin__detail {
  color: var(--muted);
  font-size: 11.5px;
}

.context-empty {
  margin: 0;
  color: var(--muted);
  font-size: 12.5px;
  line-height: 1.5;
}

.context-link-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 2px 4px;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--accent);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
}

.context-link-btn:hover:not(:disabled) {
  background: color-mix(in srgb, var(--text) 6%, transparent);
}

.context-link-btn:disabled {
  cursor: default;
  opacity: 0.7;
}

.context-link-btn:focus-visible,
.context-attach__submit:focus-visible,
.context-attach__input:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.context-attach__start {
  align-self: flex-start;
  margin-left: -4px;
}

.context-attach__form {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.context-attach__label {
  color: var(--muted);
  font-size: 11.5px;
}

.context-attach__input {
  width: 100%;
  height: 30px;
  padding: 0 9px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
}

.context-attach__actions {
  display: flex;
  align-items: center;
  gap: 6px;
}

.context-attach__submit {
  height: 26px;
  padding: 0 12px;
  border: 0;
  border-radius: var(--radius-btn);
  background: var(--accent);
  color: var(--primary-foreground, #fff);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
}

.context-attach__submit:disabled {
  cursor: default;
  opacity: 0.5;
}

.context-attach__error {
  margin: 0;
  color: var(--error);
  font-size: 11.5px;
}

.context-workspace {
  display: grid;
  grid-template-columns: 72px minmax(0, 1fr);
  gap: 4px 10px;
  margin: 0;
  font-size: 12px;
}

.context-workspace dt {
  color: var(--muted);
}

.context-workspace dd {
  display: flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
  margin: 0;
  overflow: hidden;
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.context-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  margin-top: auto;
  padding-top: 10px;
  border-top: 1px solid var(--border);
  color: var(--muted);
  font-size: 11.5px;
}

.context-spin {
  animation: context-spin 0.9s linear infinite;
}

.context-canvas :deep(.context-flash) {
  animation: context-flash 1.2s ease-out;
}

@keyframes context-spin {
  to { transform: rotate(360deg); }
}

@keyframes context-flash {
  from { box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 55%, transparent); }
  to { box-shadow: 0 0 0 2px transparent; }
}

@media (prefers-reduced-motion: reduce) {
  .context-spin,
  .context-canvas :deep(.context-flash) {
    animation: none;
  }
}
</style>
