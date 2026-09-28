<script setup lang="ts">
import { computed, ref, shallowRef } from "vue";
import { useNavigate } from "@tanstack/vue-router";
import { ShieldAlert } from "lucide-vue-next";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { useSettingsNav } from "@/composables/use-settings-nav";
import type { PermissionAsk, PermissionReply } from "@/composables/use-session-permissions";

const props = defineProps<{
  ask: PermissionAsk;
  onAnswer: (reply: PermissionReply, message?: string) => Promise<void>;
}>();

const navigate = useNavigate();
const { setActiveSection } = useSettingsNav();

// ── What the agent wants ──────────────────────────────────────────────────────

const heading = computed(() => {
  switch (props.ask.kind) {
    case "shell": return "Run a command";
    case "edit": return "Edit a file";
    case "web": return "Go online";
    default:
      return props.ask.tool === "external_directory" ? "Work outside the folder" : `Use ${props.ask.tool}`;
  }
});

interface DiffLine {
  text: string;
  kind: "add" | "del" | "hunk" | "context";
}

/** A diff's lines, coloured; the file headers a unified diff starts with are left out. */
const diffLines = computed<DiffLine[]>(() => {
  const detail = props.ask.detail;
  if (!detail) return [];
  return detail
    .split("\n")
    .filter((line) => !/^(Index: |={3,}|-{3} |\+{3} )/.test(line))
    .map((line) => ({
      text: line,
      kind: line.startsWith("@@") ? "hunk" : line.startsWith("+") ? "add" : line.startsWith("-") ? "del" : "context",
    }));
});

const added = computed(() => diffLines.value.filter((line) => line.kind === "add").length);
const removed = computed(() => diffLines.value.filter((line) => line.kind === "del").length);
// A write that leaves the file as it is sends a diff of headers only: there's nothing to show.
const changes = computed(() => added.value + removed.value > 0);

/** What "Don't ask again" covers, in words: the harness's pattern, or the whole kind of thing. */
const alwaysLabel = computed(() => {
  const patterns = props.ask.always.filter((pattern) => pattern !== "*");
  if (patterns.length > 0) return { lead: "Don't ask again for", code: patterns.join(", ") };
  switch (props.ask.kind) {
    case "edit": return { lead: "Don't ask again for file edits", code: null };
    case "shell": return { lead: "Don't ask again for commands", code: null };
    case "web": return { lead: "Don't ask again for web access", code: null };
    default: return { lead: "Don't ask again for", code: props.ask.tool };
  }
});

// ── Answering ─────────────────────────────────────────────────────────────────

const denyText = ref("");
const loading = shallowRef(false);
const error = shallowRef<string | null>(null);
const cardRef = ref<HTMLElement | null>(null);

async function answer(reply: PermissionReply): Promise<void> {
  if (loading.value) return;
  loading.value = true;
  error.value = null;
  try {
    await props.onAnswer(reply, reply === "reject" ? denyText.value : undefined);
  } catch (e) {
    error.value = e instanceof Error ? e.message : "The answer didn't reach the agent.";
  } finally {
    loading.value = false;
  }
}

function handleCardKeydown(event: KeyboardEvent): void {
  if (event.metaKey || event.ctrlKey || event.altKey) return;
  const typing = event.target instanceof HTMLInputElement;
  if (event.key === "Escape") {
    event.preventDefault();
    void answer("reject");
    return;
  }
  if (typing) {
    if (event.key === "Enter") {
      event.preventDefault();
      void answer("reject");
    }
    return;
  }
  if (event.key === "1") {
    event.preventDefault();
    void answer("once");
  } else if (event.key === "2") {
    event.preventDefault();
    void answer("always");
  } else if (event.key === "3") {
    event.preventDefault();
    focusDeny();
  }
}

function focusDeny(): void {
  cardRef.value?.querySelector<HTMLInputElement>(".pcard__deny-input")?.focus();
}

function openSettings(): void {
  setActiveSection("permissions");
  void navigate({ to: "/settings" });
}
</script>

<template>
  <article
    ref="cardRef"
    class="pcard"
    data-testid="permission-card"
    tabindex="-1"
    :aria-label="`The agent asks: ${heading}`"
    @keydown="handleCardKeydown"
  >
    <header class="pcard__head">
      <ShieldAlert
        class="pcard__head-icon"
        aria-hidden="true"
      />
      <span class="pcard__title">{{ heading }}</span>
      <span
        v-if="ask.subagent"
        class="pcard__from"
      >Subagent</span>
      <span class="pcard__needs">
        <StatusGlyph status="waiting_input" />
        Needs you
      </span>
    </header>

    <!-- A command -->
    <pre
      v-if="ask.kind === 'shell' && ask.title"
      class="pcard__cmd"
    ><span
      v-if="ask.directory"
      class="pcard__cwd"
    >{{ ask.directory }}</span><span
      class="pcard__prompt"
      aria-hidden="true"
    >$ </span>{{ ask.title }}</pre>

    <!-- A file, with what changes in it -->
    <div
      v-else-if="ask.kind === 'edit'"
      class="pcard__diff"
    >
      <div class="pcard__file">
        <span class="pcard__path">{{ ask.title ?? ask.tool }}</span>
        <span
          v-if="added"
          class="pcard__add"
        >+{{ added }}</span>
        <span
          v-if="removed"
          class="pcard__del"
        >−{{ removed }}</span>
      </div>
      <div
        v-if="changes"
        class="pcard__lines"
      >
        <span
          v-for="(line, index) in diffLines"
          :key="index"
          class="pcard__line"
          :class="`pcard__line--${line.kind}`"
        >{{ line.text || " " }}</span>
      </div>
    </div>

    <!-- An address, a folder, anything else -->
    <pre
      v-else-if="ask.title"
      class="pcard__cmd"
    >{{ ask.title }}</pre>

    <div
      class="pcard__choices"
      role="group"
      aria-label="Answer"
    >
      <button
        type="button"
        class="pcard__choice"
        data-testid="permission-allow-once"
        :disabled="loading"
        @click="answer('once')"
      >
        <span
          class="pcard__key"
          aria-hidden="true"
        >1</span>
        <span class="pcard__name">Allow once</span>
      </button>
      <button
        type="button"
        class="pcard__choice"
        data-testid="permission-allow-always"
        :disabled="loading"
        @click="answer('always')"
      >
        <span
          class="pcard__key"
          aria-hidden="true"
        >2</span>
        <span class="pcard__name">
          {{ alwaysLabel.lead }}<template v-if="alwaysLabel.code">
            <code>{{ alwaysLabel.code }}</code>
          </template>
        </span>
        <span class="pcard__scope">this session</span>
      </button>
      <div
        class="pcard__choice pcard__choice--deny"
        @click="focusDeny"
      >
        <span
          class="pcard__key"
          aria-hidden="true"
        >3</span>
        <input
          v-model="denyText"
          type="text"
          class="pcard__deny-input"
          placeholder="Deny, and tell the agent what to do instead…"
          aria-label="Deny, with an optional message for the agent"
          data-testid="permission-deny-input"
          :disabled="loading"
        >
        <button
          type="button"
          class="pcard__deny"
          data-testid="permission-deny"
          :disabled="loading"
          @click.stop="answer('reject')"
        >
          Deny
        </button>
      </div>
    </div>

    <p
      v-if="error"
      class="pcard__error"
      role="alert"
    >
      {{ error }}
    </p>

    <footer class="pcard__foot">
      <span aria-hidden="true">1 or 2 answers · 3 then Enter denies · Esc denies</span>
      <button
        type="button"
        class="pcard__settings"
        @click="openSettings"
      >
        Permission settings
      </button>
    </footer>
  </article>
</template>

<style scoped>
/* Waiting on you: the question card's amber, since both are the reason the session needs you. */
.pcard {
  display: grid;
  gap: 12px;
  margin-top: 10px;
  padding: 12px;
  border: 1px solid color-mix(in srgb, var(--status-waiting) 40%, var(--border));
  border-radius: var(--radius-card);
  background: color-mix(in srgb, var(--status-waiting) 5%, var(--card-bg));
  animation: pcard-rise 180ms ease-out;
}

@keyframes pcard-rise {
  from {
    opacity: 0;
    transform: translateY(4px);
  }
}

@media (prefers-reduced-motion: reduce) {
  .pcard {
    animation: none;
  }
}

.pcard:focus {
  outline: none;
}

.pcard__head {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.pcard__head-icon {
  width: 15px;
  height: 15px;
  flex-shrink: 0;
  color: var(--status-waiting);
}

.pcard__title {
  min-width: 0;
  overflow: hidden;
  color: var(--text);
  font-size: 13px;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.pcard__from {
  flex-shrink: 0;
  padding: 0 8px;
  border: 1px solid var(--border);
  border-radius: 999px;
  color: var(--muted);
  font-size: 11.5px;
}

.pcard__needs {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 6px;
  margin-left: auto;
  color: var(--status-waiting);
  font-size: 12px;
  font-weight: 600;
}

.pcard__cmd {
  margin: 0;
  padding: 9px 12px;
  overflow-x: auto;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--panel-bg);
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 12.5px;
  white-space: pre;
}

.pcard__cwd {
  display: block;
  margin-bottom: 2px;
  color: var(--muted);
  font-size: 11.5px;
}

.pcard__prompt {
  color: var(--muted);
  user-select: none;
}

.pcard__diff {
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--panel-bg);
  font-family: var(--font-mono-stack);
  font-size: 12px;
}

.pcard__file {
  display: flex;
  gap: 8px;
  min-width: 0;
  padding: 6px 12px;
  color: var(--muted);
  font-size: 11.5px;
}

.pcard__path {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.pcard__add {
  color: var(--diff-add);
}

.pcard__del {
  color: var(--diff-del);
}

.pcard__lines {
  max-height: 240px;
  overflow: auto;
  border-top: 1px solid var(--border);
}

.pcard__line {
  display: block;
  padding: 0 12px;
  white-space: pre;
}

.pcard__line--add {
  background: color-mix(in srgb, var(--diff-add) 10%, transparent);
}

.pcard__line--del {
  background: color-mix(in srgb, var(--diff-del) 10%, transparent);
}

.pcard__line--hunk {
  color: var(--muted);
}

.pcard__choices {
  display: grid;
  gap: 4px;
}

.pcard__choice {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  min-width: 0;
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--panel-bg);
  color: var(--text);
  font: inherit;
  text-align: left;
  cursor: pointer;
  transition: border-color var(--transition), background var(--transition);
}

.pcard__choice:hover:not(:disabled) {
  border-color: color-mix(in srgb, var(--accent) 35%, var(--border));
}

.pcard__choice:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -1px;
}

.pcard__choice:disabled {
  opacity: 0.55;
  cursor: default;
}

.pcard__choice--deny {
  cursor: text;
}

.pcard__choice--deny:focus-within {
  border-color: var(--accent);
}

.pcard__key {
  display: grid;
  place-items: center;
  width: 18px;
  height: 18px;
  flex-shrink: 0;
  border: 1px solid var(--border);
  border-radius: 5px;
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11px;
}

.pcard__name {
  min-width: 0;
  overflow: hidden;
  font-size: 13px;
  font-weight: 500;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.pcard__name code {
  margin-left: 4px;
  padding: 0 4px;
  border-radius: 4px;
  background: color-mix(in srgb, var(--text) 6%, transparent);
  font-family: var(--font-mono-stack);
  font-size: 12px;
}

.pcard__scope {
  flex-shrink: 0;
  margin-left: auto;
  color: var(--muted);
  font-size: 12px;
}

.pcard__deny-input {
  flex: 1;
  min-width: 0;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 13px;
  outline: none;
}

.pcard__deny-input::placeholder {
  color: var(--muted);
}

.pcard__deny {
  flex-shrink: 0;
  height: 24px;
  padding: 0 10px;
  border: 1px solid transparent;
  border-radius: 999px;
  background: transparent;
  color: var(--muted);
  font: inherit;
  font-size: 12px;
  cursor: pointer;
}

.pcard__deny:hover:not(:disabled) {
  background: color-mix(in srgb, var(--error) 10%, transparent);
  color: var(--error);
}

.pcard__deny:focus-visible,
.pcard__settings:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.pcard__error {
  margin: 0;
  color: var(--error);
  font-size: 12px;
}

.pcard__foot {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  color: var(--muted);
  font-size: 11px;
}

.pcard__settings {
  margin-left: auto;
  padding: 0;
  border: 0;
  background: none;
  color: var(--accent);
  font: inherit;
  font-size: 11px;
  cursor: pointer;
}

.pcard__settings:hover {
  text-decoration: underline;
}

@media (max-width: 520px) {
  .pcard__scope,
  .pcard__foot > span {
    display: none;
  }
}
</style>
