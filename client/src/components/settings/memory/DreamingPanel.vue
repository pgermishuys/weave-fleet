<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from "vue";
import { useNavigate } from "@tanstack/vue-router";
import { ChevronRight, LoaderCircle, Moon } from "lucide-vue-next";
import { useDreamingFeature } from "@/composables/use-dreaming-feature";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import {
  DREAM_POLL_MS,
  dreamKindLabel,
  dreamListLabel,
  groupDreamChanges,
  isDreamBusy,
  listDreams,
  startDream,
  undoDreamChange,
  type DreamChangeView,
  type DreamSessionRef,
  type DreamView,
} from "@/lib/dreaming";

const props = defineProps<{ repository: string | null }>();
/** A change was undone or a dream finished: the notes list should be read again. */
const emit = defineEmits<{ changed: [] }>();

const { isDreamingEnabled } = useDreamingFeature();
const { enabledHarnesses } = useEnabledHarnesses();
const navigate = useNavigate();

const HARNESS_KEY = "weave:dreaming-harness";
const dream = shallowRef<DreamView | null>(null);
const error = ref<string | null>(null);
const isStarting = ref(false);
const undoing = ref<string | null>(null);
const open = ref<Set<string>>(new Set());
const showSkipped = ref(false);
const chosen = ref<string>(localStorage.getItem(HARNESS_KEY) ?? "");

const harnessType = computed(() => {
  if (enabledHarnesses.value.some((harness) => harness.type === chosen.value)) return chosen.value;
  return enabledHarnesses.value.find((harness) => harness.type === "opencode2")?.type ?? enabledHarnesses.value[0]?.type ?? "";
});
const busy = computed(() => isDreamBusy(dream.value));
const groups = computed(() => groupDreamChanges(dream.value?.changes ?? []));
const changedCount = computed(() => groups.value.applied.filter((change) => !change.undone).length);

const when = new Intl.DateTimeFormat(undefined, { day: "numeric", month: "short", hour: "numeric", minute: "2-digit" });
const harnessLabel = (type: string | null) => enabledHarnesses.value.find((harness) => harness.type === type)?.displayName ?? type ?? "";

function pickHarness(type: string): void {
  chosen.value = type;
  localStorage.setItem(HARNESS_KEY, type);
}

function openSession(id: string): void {
  void navigate({ to: "/sessions/$id", params: { id }, search: { instanceId: undefined, parentSessionId: undefined } });
}

async function load(): Promise<void> {
  if (!isDreamingEnabled.value || !props.repository) {
    dream.value = null;
    return;
  }
  const repository = props.repository;
  try {
    const dreams = await listDreams(repository);
    if (repository === props.repository) dream.value = dreams[0] ?? null;
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "Couldn't load the dreams.";
  }
}

async function dreamNow(): Promise<void> {
  if (!props.repository || isStarting.value) return;
  isStarting.value = true;
  error.value = null;
  try {
    dream.value = await startDream(props.repository, harnessType.value ? { harnessType: harnessType.value } : {});
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "Couldn't start the dream.";
  } finally {
    isStarting.value = false;
  }
}

async function undo(change: DreamChangeView): Promise<void> {
  if (!dream.value || undoing.value) return;
  undoing.value = change.id;
  error.value = null;
  try {
    dream.value = await undoDreamChange(dream.value.id, change.id);
    emit("changed");
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "Couldn't undo the change.";
  } finally {
    undoing.value = null;
  }
}

function toggleWhy(id: string): void {
  const next = new Set(open.value);
  if (!next.delete(id)) next.add(id);
  open.value = next;
}

let timer: ReturnType<typeof setInterval> | null = null;
function stopPolling(): void {
  if (timer) clearInterval(timer);
  timer = null;
}

watch(busy, (isBusy, wasBusy) => {
  stopPolling();
  if (isBusy) {
    timer = setInterval(() => void load(), DREAM_POLL_MS);
  } else if (wasBusy) {
    emit("changed");
  }
});
watch([isDreamingEnabled, () => props.repository], () => void load(), { immediate: true });
onBeforeUnmount(stopPolling);

function sessionLabel(session: DreamSessionRef): string {
  return session.harnessType ? `${session.title} · ${harnessLabel(session.harnessType)}` : session.title;
}
</script>

<template>
  <section
    v-if="isDreamingEnabled && repository"
    class="mt-5 rounded-card border border-border bg-main-bg p-4"
    data-testid="dreaming-panel"
  >
    <div class="flex flex-wrap items-center justify-between gap-2">
      <p class="flex items-center gap-2 text-sm font-medium text-text">
        <Moon
          :size="15"
          class="text-accent"
          aria-hidden="true"
        />
        Dreaming
      </p>
      <div class="flex items-center gap-2">
        <select
          :value="harnessType"
          aria-label="Harness for dreaming"
          data-testid="dreaming-harness"
          class="rounded-btn border border-border bg-main-bg px-2 py-1 text-sm text-text outline-none focus:border-accent"
          @change="pickHarness(($event.target as HTMLSelectElement).value)"
        >
          <option
            v-for="harness in enabledHarnesses"
            :key="harness.type"
            :value="harness.type"
          >
            {{ harness.displayName }}
          </option>
        </select>
        <button
          type="button"
          class="rounded-btn bg-accent px-3 py-1 text-sm font-medium text-white disabled:opacity-60"
          :disabled="busy || isStarting"
          data-testid="dream-now"
          @click="dreamNow"
        >
          Dream now
        </button>
      </div>
    </div>

    <p
      v-if="error"
      class="mt-3 text-xs text-red-300"
      role="alert"
    >
      {{ error }}
    </p>

    <template v-if="dream">
      <p
        v-if="busy"
        class="mt-3 flex items-center gap-2 text-sm text-muted"
        data-testid="dream-running"
      >
        <LoaderCircle
          :size="14"
          class="animate-spin"
          aria-hidden="true"
        />
        Dreaming…
        <template v-if="dream.readSessions.length > 0">
          reading {{ dream.readSessions.length }} {{ dream.readSessions.length === 1 ? "session" : "sessions" }}
        </template>
        <button
          v-if="dream.sessionId"
          type="button"
          class="text-accent hover:underline"
          @click="openSession(dream.sessionId)"
        >
          Open the dreaming session
        </button>
      </p>

      <template v-else>
        <p
          v-if="dream.status === 'failed'"
          class="mt-3 text-sm text-red-300"
          data-testid="dream-failed"
        >
          The last dream failed: {{ dream.error ?? "no reason given." }}
        </p>
        <div
          v-else
          class="mt-3"
        >
          <p class="text-sm font-medium text-text">
            Last dream · {{ dream.repositoryName }} · {{ changedCount }} {{ changedCount === 1 ? "note" : "notes" }} changed
          </p>
          <p class="mt-0.5 text-xs text-muted">
            Read {{ dream.readSessions.length }} {{ dream.readSessions.length === 1 ? "session" : "sessions" }}
            <template v-if="dream.finished">
              · {{ when.format(new Date(dream.finished)) }}
            </template>
            <template v-if="dream.sessionId">
              ·
              <button
                type="button"
                class="text-accent hover:underline"
                @click="openSession(dream.sessionId)"
              >
                Open the dreaming session
              </button>
            </template>
          </p>
        </div>

        <ul class="mt-3 flex flex-col gap-2">
          <li
            v-for="change in [...groups.applied, ...groups.left]"
            :key="change.id"
            class="rounded-card border bg-card-bg p-3"
            :class="change.kind === 'left' ? 'border-amber-500/40' : 'border-border'"
            data-testid="dream-change"
          >
            <p class="flex items-center gap-2 text-[11px] font-semibold uppercase tracking-wide text-muted">
              <span :class="change.kind === 'left' ? 'text-amber-300' : 'text-accent'">{{ dreamKindLabel(change.kind) }}</span>
              <template v-if="change.list">
                · {{ dreamListLabel(change.list) }}
              </template>
            </p>

            <template v-if="change.kind === 'left'">
              <p
                v-if="change.text"
                class="mt-1 text-sm text-text"
              >
                {{ change.text }}
              </p>
              <p class="mt-1 text-xs text-muted">
                {{ change.reason ?? change.why }}
              </p>
            </template>

            <template v-else>
              <p class="mt-1 text-xs text-muted">
                {{ change.why }}
              </p>
              <template v-if="change.kind === 'rewritten' && change.replaced[0]">
                <p class="mt-2 text-sm text-muted line-through opacity-70">
                  {{ change.replaced[0].text }}
                </p>
                <p class="mt-1 text-[11px] text-muted">
                  now reads
                </p>
              </template>
              <template v-if="change.kind === 'merged'">
                <p
                  v-for="removed in change.replaced"
                  :key="removed.id"
                  class="mt-2 text-sm text-muted line-through opacity-70"
                >
                  {{ removed.text }}
                </p>
                <p class="mt-1 text-[11px] text-muted">
                  kept
                </p>
              </template>
              <p
                v-if="change.kind === 'retired' && change.replaced[0]"
                class="mt-2 text-sm text-muted line-through opacity-70"
              >
                {{ change.replaced[0].text }}
              </p>
              <p
                v-if="change.text && change.kind !== 'retired'"
                class="mt-1 text-sm text-text"
              >
                {{ change.text }}
              </p>

              <div class="mt-2 flex items-center justify-between gap-2">
                <button
                  type="button"
                  class="flex items-center gap-1 text-xs text-muted hover:text-text"
                  :aria-expanded="open.has(change.id)"
                  data-testid="dream-why"
                  @click="toggleWhy(change.id)"
                >
                  <ChevronRight
                    :size="12"
                    :class="open.has(change.id) ? 'rotate-90' : ''"
                    aria-hidden="true"
                  />
                  Why
                </button>
                <button
                  type="button"
                  class="rounded-btn border border-border px-2 py-0.5 text-xs text-text disabled:opacity-60"
                  :disabled="change.undone || undoing === change.id"
                  data-testid="dream-undo"
                  @click="undo(change)"
                >
                  {{ change.undone ? "Undone" : "Undo" }}
                </button>
              </div>
              <div
                v-if="open.has(change.id)"
                class="mt-2 border-t border-border pt-2 text-xs text-muted"
                data-testid="dream-evidence"
              >
                <p>{{ change.sessions.length }} of {{ change.ofSessions }} sessions</p>
                <ul class="mt-1 flex flex-col gap-0.5">
                  <li
                    v-for="session in change.sessions"
                    :key="session.id"
                  >
                    <button
                      type="button"
                      class="text-left text-accent hover:underline"
                      @click="openSession(session.id)"
                    >
                      {{ sessionLabel(session) }}
                    </button>
                  </li>
                </ul>
                <pre
                  v-if="change.quote"
                  class="mt-2 whitespace-pre-wrap rounded-btn bg-main-bg p-2 font-mono text-[11px] text-text"
                >{{ change.quote }}</pre>
              </div>
            </template>
          </li>
        </ul>

        <div
          v-if="groups.skipped.length > 0"
          class="mt-3"
        >
          <button
            type="button"
            class="flex items-center gap-1 text-xs text-muted hover:text-text"
            :aria-expanded="showSkipped"
            data-testid="dream-skipped"
            @click="showSkipped = !showSkipped"
          >
            <ChevronRight
              :size="12"
              :class="showSkipped ? 'rotate-90' : ''"
              aria-hidden="true"
            />
            Seen in one session only, not changed ({{ groups.skipped.length }})
          </button>
          <ul
            v-if="showSkipped"
            class="mt-2 flex flex-col gap-1 text-xs text-muted"
          >
            <li
              v-for="change in groups.skipped"
              :key="change.id"
            >
              {{ change.text ?? change.why }}
            </li>
          </ul>
        </div>
      </template>
    </template>
  </section>
</template>
