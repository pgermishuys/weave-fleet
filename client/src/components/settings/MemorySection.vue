<script setup lang="ts">
import { computed, onMounted, ref, shallowRef, watch } from "vue";
import { Cable, ChevronRight, Coins, Eye, FileText, FolderGit2, Hourglass, LoaderCircle, Pencil, Pin, Plus, Search, Server, TriangleAlert, X } from "lucide-vue-next";
import {
  addMemoryNote,
  clearMemory,
  forgetMemoryNote,
  getMemory,
  keepMemoryNote,
  listMemoryNotes,
  memoryKindLabel,
  memoryLifeLabel,
  setMemoryEnabled,
  updateMemoryNote,
  type MemoryListName,
  type MemoryNote,
  type MemoryNotes,
  type MemoryOverview,
} from "@/lib/agent-memory";

const overview = shallowRef<MemoryOverview | null>(null);
const notes = shallowRef<MemoryNotes | null>(null);
const repository = ref<string | null>(null);
const search = ref("");
const error = ref<string | null>(null);
const isLoading = ref(true);
const isSaving = ref(false);

const adding = ref<MemoryListName | null>(null);
const draft = ref("");
const editingId = ref<string | null>(null);
const editText = ref("");
const confirmingClear = ref(false);
const showExpired = ref(false);

const enabled = computed(() => overview.value?.enabled ?? false);
const repositoryName = computed(() => overview.value?.repositories.find((item) => item.path === repository.value)?.name ?? "this repository");
const noteCount = computed(() => (notes.value ? notes.value.repositoryNotes.length + notes.value.machineNotes.length : 0));

function matches(note: MemoryNote): boolean {
  const query = search.value.trim().toLowerCase();
  return !query || note.text.toLowerCase().includes(query) || (note.sessionTitle ?? "").toLowerCase().includes(query);
}

const repositoryNotes = computed(() => (notes.value?.repositoryNotes ?? []).filter(matches));
const machineNotes = computed(() => (notes.value?.machineNotes ?? []).filter(matches));
const expiredNotes = computed(() => (notes.value?.expiredNotes ?? []).filter(matches));

const savedDate = new Intl.DateTimeFormat(undefined, { day: "numeric", month: "short" });

function origin(note: MemoryNote): string {
  const date = savedDate.format(new Date(note.updated));
  if (note.kind === "added") return `Added by you on ${date}`;
  return note.sessionTitle ? `Saved ${date} by “${note.sessionTitle}”` : `Saved ${date}`;
}

async function run(action: () => Promise<void>): Promise<void> {
  if (isSaving.value) return;
  isSaving.value = true;
  error.value = null;
  try {
    await action();
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "Something went wrong.";
  } finally {
    isSaving.value = false;
  }
}

async function loadNotes(): Promise<void> {
  notes.value = await listMemoryNotes(repository.value);
}

async function load(): Promise<void> {
  isLoading.value = true;
  error.value = null;
  try {
    overview.value = await getMemory();
    repository.value ??= overview.value.repositories[0]?.path ?? null;
    await loadNotes();
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "Couldn't load memory.";
  } finally {
    isLoading.value = false;
  }
}

async function refresh(): Promise<void> {
  overview.value = await getMemory();
  await loadNotes();
}

function toggle(): Promise<void> {
  return run(async () => {
    overview.value = await setMemoryEnabled(!enabled.value);
    confirmingClear.value = false;
  });
}

function startAdding(list: MemoryListName): void {
  adding.value = list;
  draft.value = "";
}

function saveDraft(): Promise<void> {
  const list = adding.value;
  if (!list || !draft.value.trim()) return Promise.resolve();
  return run(async () => {
    await addMemoryNote(list, list === "repository" ? repository.value : null, draft.value.trim());
    adding.value = null;
    await refresh();
  });
}

function startEditing(note: MemoryNote): void {
  editingId.value = note.id;
  editText.value = note.text;
}

function saveEdit(note: MemoryNote): Promise<void> {
  if (!editText.value.trim()) return Promise.resolve();
  return run(async () => {
    await updateMemoryNote(note.id, editText.value.trim());
    editingId.value = null;
    await loadNotes();
  });
}

function keep(note: MemoryNote): Promise<void> {
  return run(async () => {
    await keepMemoryNote(note.id);
    await loadNotes();
  });
}

function forget(note: MemoryNote): Promise<void> {
  return run(async () => {
    await forgetMemoryNote(note.id);
    await refresh();
  });
}

function clearList(list: MemoryListName): Promise<void> {
  return run(async () => {
    await clearMemory(list, list === "repository" ? repository.value : null);
    await refresh();
  });
}

function clearAll(): Promise<void> {
  return run(async () => {
    await clearMemory("all");
    confirmingClear.value = false;
    await refresh();
  });
}

watch(repository, (next, previous) => {
  if (previous !== undefined && next !== previous) void run(loadNotes);
});

onMounted(load);
</script>

<template>
  <section class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
    <div class="flex flex-col gap-1">
      <h2 class="text-lg font-semibold text-text">
        Memory
      </h2>
      <p class="text-sm text-muted">
        Let agents keep notes about each repository (test commands, conventions, your corrections) and about this machine
        (disk space, memory, crashes, tools that don't work here). New sessions start with both, so agents get better the
        longer you work with them.
      </p>
    </div>

    <div
      v-if="isLoading"
      class="mt-5 flex items-center gap-2 text-sm text-muted"
    >
      <LoaderCircle
        :size="16"
        class="animate-spin"
        aria-hidden="true"
      />
      <span>Loading memory…</span>
    </div>

    <template v-else-if="overview">
      <div class="mt-5 flex items-start justify-between gap-4 rounded-card border border-border bg-main-bg p-4">
        <div>
          <p class="text-sm font-medium text-text">
            Remember what agents learn
          </p>
          <p
            class="mt-1 text-xs text-muted"
            data-testid="memory-state"
          >
            {{ enabled
              ? "On. New sessions start with their repository's notes and this machine's notes."
              : "Off. Agents save nothing and sessions carry nothing extra." }}
          </p>
        </div>
        <div class="flex items-center gap-2">
          <LoaderCircle
            v-if="isSaving"
            :size="16"
            class="animate-spin text-muted"
            aria-hidden="true"
          />
          <button
            type="button"
            role="switch"
            :aria-checked="enabled"
            :disabled="isSaving"
            aria-label="Remember what agents learn"
            data-testid="memory-switch"
            class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-main-bg disabled:cursor-not-allowed disabled:opacity-60"
            :class="enabled ? 'bg-accent' : 'bg-border'"
            @click="toggle"
          >
            <span
              class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out"
              :class="enabled ? 'translate-x-5' : 'translate-x-0'"
            />
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

      <div
        v-if="!enabled"
        class="mt-4 grid gap-3 sm:grid-cols-2"
      >
        <p class="memory-fact">
          <Coins
            :size="15"
            aria-hidden="true"
          />
          <span><b>Costs tokens.</b> Every request a session sends carries its notes: about 400 tokens of rules plus
            30–50 per note, up to {{ overview.maxRepositoryNotes }} repository and {{ overview.maxMachineNotes }} machine notes.</span>
        </p>
        <p class="memory-fact">
          <Eye
            :size="15"
            aria-hidden="true"
          />
          <span><b>Saved automatically, never quietly.</b> Each note shows in a small notice with Undo, and stays in the
            conversation. Edit or forget any note here.</span>
        </p>
        <p class="memory-fact">
          <FileText
            :size="15"
            aria-hidden="true"
          />
          <span><b>Plain Markdown files</b> in Fleet's data folder, one per note, never in your repository.</span>
        </p>
        <p class="memory-fact">
          <FolderGit2
            :size="15"
            aria-hidden="true"
          />
          <span><b>Two lists.</b> Repository notes reach only that repository's sessions, worktrees included. Machine
            notes reach every session on this machine.</span>
        </p>
      </div>

      <template v-else-if="notes">
        <div class="mt-5 flex flex-wrap items-center justify-between gap-2">
          <div class="flex flex-wrap items-center gap-2">
            <label class="memory-field">
              <FolderGit2
                :size="14"
                aria-hidden="true"
              />
              <span class="sr-only">Repository</span>
              <select
                v-model="repository"
                class="bg-transparent text-sm text-text outline-none"
                data-testid="memory-repository"
              >
                <option
                  v-if="overview.repositories.length === 0"
                  :value="null"
                >
                  No repositories yet
                </option>
                <option
                  v-for="item in overview.repositories"
                  :key="item.path"
                  :value="item.path"
                  :title="item.path"
                >
                  {{ item.name }}
                </option>
              </select>
            </label>
            <label class="memory-field">
              <Search
                :size="14"
                aria-hidden="true"
              />
              <span class="sr-only">Search notes</span>
              <input
                v-model="search"
                type="search"
                placeholder="Search notes"
                class="w-40 bg-transparent text-sm text-text outline-none placeholder:text-muted"
              >
            </label>
          </div>
          <span
            class="text-xs text-muted"
            data-testid="memory-count"
          >{{ noteCount }} {{ noteCount === 1 ? "note" : "notes" }} · about {{ notes.tokens.toLocaleString() }} tokens per request</span>
        </div>

        <template
          v-for="group in ([
            { list: 'repository', title: `Repository · ${repositoryName}`, items: repositoryNotes, total: notes.repositoryNotes.length, max: overview.maxRepositoryNotes },
            { list: 'machine', title: 'This machine · every repository', items: machineNotes, total: notes.machineNotes.length, max: overview.maxMachineNotes },
          ] as const)"
          :key="group.list"
        >
          <div class="mt-5 flex items-center justify-between gap-2">
            <p class="flex items-center gap-2 text-[11px] font-semibold uppercase tracking-wide text-muted">
              <component
                :is="group.list === 'machine' ? Server : FolderGit2"
                :size="13"
                aria-hidden="true"
              />
              {{ group.title }}
              <span class="font-normal normal-case tracking-normal">{{ group.total }}/{{ group.max }}</span>
            </p>
            <div class="flex items-center gap-1">
              <button
                v-if="group.list === 'machine' || repository"
                type="button"
                class="memory-link"
                :data-testid="`memory-add-${group.list}`"
                @click="startAdding(group.list)"
              >
                <Plus
                  :size="13"
                  aria-hidden="true"
                />
                Add a note
              </button>
              <button
                v-if="group.total > 0"
                type="button"
                class="memory-link"
                :disabled="isSaving"
                @click="clearList(group.list)"
              >
                Clear these {{ group.total }}
              </button>
            </div>
          </div>

          <div
            v-if="adding === group.list"
            class="mt-2 flex flex-col gap-2 rounded-card border border-border bg-main-bg p-3"
          >
            <textarea
              v-model="draft"
              rows="2"
              maxlength="400"
              :placeholder="group.list === 'machine' ? 'Something true for every repository on this machine' : `Something true for ${repositoryName} on any computer`"
              class="w-full resize-none rounded-md border border-border bg-card-bg p-2 text-sm text-text outline-none focus:border-accent"
              :data-testid="`memory-draft-${group.list}`"
              @keydown.enter.exact.prevent="saveDraft"
              @keydown.esc="adding = null"
            />
            <div class="flex gap-2">
              <button
                type="button"
                class="rounded-md bg-accent px-3 py-1 text-xs font-medium text-white disabled:opacity-60"
                :disabled="isSaving || !draft.trim()"
                @click="saveDraft"
              >
                Save note
              </button>
              <button
                type="button"
                class="memory-link"
                @click="adding = null"
              >
                Cancel
              </button>
            </div>
          </div>

          <ul
            v-if="group.items.length"
            class="mt-2 flex flex-col overflow-hidden rounded-card border border-border bg-main-bg"
            :data-testid="`memory-list-${group.list}`"
          >
            <li
              v-for="note in group.items"
              :key="note.id"
              class="flex items-start justify-between gap-3 border-t border-border p-3 first:border-t-0"
            >
              <div class="min-w-0 flex-1">
                <textarea
                  v-if="editingId === note.id"
                  v-model="editText"
                  rows="2"
                  maxlength="400"
                  class="w-full resize-none rounded-md border border-border bg-card-bg p-2 text-sm text-text outline-none focus:border-accent"
                  aria-label="Edit note"
                  @keydown.enter.exact.prevent="saveEdit(note)"
                  @keydown.esc="editingId = null"
                />
                <div
                  v-if="editingId === note.id"
                  class="mt-1 flex gap-2"
                >
                  <button
                    type="button"
                    class="rounded-md bg-accent px-3 py-1 text-xs font-medium text-white disabled:opacity-60"
                    :disabled="isSaving || !editText.trim()"
                    @click="saveEdit(note)"
                  >
                    Save
                  </button>
                  <button
                    type="button"
                    class="memory-link"
                    @click="editingId = null"
                  >
                    Cancel
                  </button>
                </div>
                <p
                  v-else
                  class="text-sm text-text [overflow-wrap:anywhere]"
                >
                  {{ note.text }}
                </p>
                <p class="mt-1 flex flex-wrap items-center gap-x-2 gap-y-1 text-[11px] text-muted">
                  <span
                    class="memory-kind"
                    :class="`memory-kind--${note.kind}`"
                  >{{ memoryKindLabel(note.kind) }}</span>
                  {{ origin(note) }}
                  <span
                    v-if="memoryLifeLabel(note)"
                    class="memory-life"
                    :data-testid="`memory-life-${note.id}`"
                  >
                    <Hourglass
                      :size="11"
                      aria-hidden="true"
                    />
                    {{ memoryLifeLabel(note) }}
                  </span>
                </p>
              </div>
              <div class="flex shrink-0 items-center gap-1">
                <button
                  v-if="note.lifetime != null"
                  type="button"
                  class="memory-icon-btn"
                  :aria-label="`Keep note for good: ${note.text}`"
                  title="Keep for good: it won't expire"
                  :disabled="isSaving"
                  :data-testid="`memory-keep-${note.id}`"
                  @click="keep(note)"
                >
                  <Pin
                    :size="14"
                    aria-hidden="true"
                  />
                </button>
                <button
                  type="button"
                  class="memory-icon-btn"
                  :aria-label="`Edit note: ${note.text}`"
                  @click="startEditing(note)"
                >
                  <Pencil
                    :size="14"
                    aria-hidden="true"
                  />
                </button>
                <button
                  type="button"
                  class="memory-icon-btn"
                  :aria-label="`Forget note: ${note.text}`"
                  :disabled="isSaving"
                  @click="forget(note)"
                >
                  <X
                    :size="14"
                    aria-hidden="true"
                  />
                </button>
              </div>
            </li>
          </ul>
          <p
            v-else-if="adding !== group.list"
            class="mt-2 rounded-card border border-dashed border-border p-3 text-xs text-muted"
          >
            {{ search ? "No notes match." : group.list === 'machine'
              ? "No notes about this machine yet. Agents save one when something fails and they find what works."
              : `No notes for ${repositoryName} yet. Say “remember …” in a session, or add one here.` }}
          </p>
        </template>

        <div
          v-if="expiredNotes.length"
          class="mt-5"
          data-testid="memory-expired"
        >
          <button
            type="button"
            class="flex items-center gap-2 text-[11px] font-semibold uppercase tracking-wide text-muted hover:text-text"
            :aria-expanded="showExpired"
            data-testid="memory-expired-toggle"
            @click="showExpired = !showExpired"
          >
            <ChevronRight
              :size="13"
              class="transition-transform"
              :class="showExpired ? 'rotate-90' : ''"
              aria-hidden="true"
            />
            Expired
            <span class="font-normal normal-case tracking-normal">{{ expiredNotes.length }}</span>
          </button>
          <template v-if="showExpired">
            <p class="mt-2 text-xs text-muted">
              Sessions no longer read these. A learned note lasts a week of use, so a lesson whose cause is gone drops
              out. If the problem comes back, the agent learns it again and the note returns for twice as long. Keep one
              you know will stay true.
            </p>
            <ul class="mt-2 flex flex-col overflow-hidden rounded-card border border-border bg-main-bg">
              <li
                v-for="note in expiredNotes"
                :key="note.id"
                class="flex items-start justify-between gap-3 border-t border-border p-3 first:border-t-0"
              >
                <div class="min-w-0 flex-1">
                  <p class="text-sm text-muted [overflow-wrap:anywhere]">
                    {{ note.text }}
                  </p>
                  <p class="mt-1 flex flex-wrap items-center gap-x-2 gap-y-1 text-[11px] text-muted">
                    <component
                      :is="note.list === 'machine' ? Server : FolderGit2"
                      :size="11"
                      aria-hidden="true"
                    />
                    {{ note.list === 'machine' ? 'This machine' : repositoryName }}
                    <span class="memory-life">
                      <Hourglass
                        :size="11"
                        aria-hidden="true"
                      />
                      {{ memoryLifeLabel(note) }}
                    </span>
                  </p>
                </div>
                <div class="flex shrink-0 items-center gap-1">
                  <button
                    type="button"
                    class="memory-link"
                    :disabled="isSaving"
                    :aria-label="`Bring back and keep: ${note.text}`"
                    :data-testid="`memory-keep-${note.id}`"
                    @click="keep(note)"
                  >
                    <Pin
                      :size="13"
                      aria-hidden="true"
                    />
                    Keep
                  </button>
                  <button
                    type="button"
                    class="memory-icon-btn"
                    :aria-label="`Forget note: ${note.text}`"
                    :disabled="isSaving"
                    @click="forget(note)"
                  >
                    <X
                      :size="14"
                      aria-hidden="true"
                    />
                  </button>
                </div>
              </li>
            </ul>
          </template>
        </div>

        <div class="mt-5 flex flex-wrap items-center justify-between gap-3 border-t border-border pt-4">
          <template v-if="!confirmingClear">
            <span class="text-xs text-muted">Clear every note, in every repository and for this machine.</span>
            <button
              type="button"
              class="rounded-md border border-red-400/40 px-3 py-1 text-xs font-medium text-red-400"
              data-testid="memory-clear-all"
              @click="confirmingClear = true"
            >
              Clear all memory
            </button>
          </template>
          <div
            v-else
            class="flex w-full flex-col gap-2 rounded-card border border-red-400/40 bg-red-500/5 p-3 text-xs text-muted"
            role="alertdialog"
            aria-label="Clear all memory"
          >
            <span><b class="text-text">Delete every note?</b> Sessions already running keep what they last read. This can't be undone.</span>
            <div class="flex gap-2">
              <button
                type="button"
                class="rounded-md bg-red-500 px-3 py-1 font-medium text-white disabled:opacity-60"
                :disabled="isSaving"
                data-testid="memory-clear-all-confirm"
                @click="clearAll"
              >
                Delete all notes
              </button>
              <button
                type="button"
                class="memory-link"
                @click="confirmingClear = false"
              >
                Cancel
              </button>
            </div>
          </div>
        </div>
      </template>

      <div class="mt-5 rounded-card border border-border bg-main-bg p-4 text-xs">
        <p class="flex items-center gap-2 font-medium text-text">
          <Cable
            :size="14"
            aria-hidden="true"
          />
          Harnesses
        </p>
        <ul class="mt-2 flex flex-col text-muted">
          <li class="flex justify-between gap-3 border-t border-border py-2 first:border-t-0">
            <span>OpenCode and OpenCode 2</span><span>Read and save notes</span>
          </li>
          <li class="border-t border-border py-2">
            <span class="flex justify-between gap-3"><span>Claude Code</span><span>Reads notes</span></span>
            <span
              class="mt-1 flex items-start gap-2"
              data-testid="memory-claude-warning"
            >
              <TriangleAlert
                :size="13"
                class="mt-0.5 shrink-0 text-[var(--status-waiting)]"
                aria-hidden="true"
              />
              Claude Code also keeps its own memory in ~/.claude. The two can hold the same lesson twice or disagree. For one
              memory, turn Claude Code's own memory off in its settings.
            </span>
          </li>
          <li class="flex justify-between gap-3 border-t border-border py-2">
            <span>Pi</span><span>Not yet</span>
          </li>
        </ul>
      </div>
    </template>

    <p
      v-else-if="error"
      class="mt-3 text-xs text-red-300"
      role="alert"
    >
      {{ error }}
    </p>
  </section>
</template>

<style scoped>
.memory-fact {
  display: flex;
  gap: 10px;
  margin: 0;
  font-size: 12px;
  line-height: 1.5;
  color: var(--muted);
}

.memory-fact > svg {
  flex-shrink: 0;
  margin-top: 2px;
  color: var(--accent);
}

.memory-fact b {
  color: var(--text);
  font-weight: 600;
}

.memory-field {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 5px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--main-bg);
  color: var(--muted);
}

.memory-field:focus-within {
  border-color: var(--accent);
}

.memory-link {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 6px;
  border-radius: 4px;
  font-size: 12px;
  color: var(--muted);
}

.memory-link:hover:not(:disabled) {
  color: var(--text);
}

.memory-link:focus-visible,
.memory-icon-btn:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.memory-icon-btn {
  display: grid;
  place-items: center;
  width: 26px;
  height: 26px;
  border-radius: var(--radius-btn);
  color: var(--muted);
}

.memory-icon-btn:hover:not(:disabled) {
  background: var(--bg);
  color: var(--text);
}

.memory-kind {
  padding: 0 7px;
  border-radius: 999px;
  font-weight: 600;
  background: var(--accent-dim);
  color: var(--accent);
}

.memory-life {
  display: inline-flex;
  align-items: center;
  gap: 3px;
}

.memory-kind--from-you {
  background: color-mix(in srgb, var(--queued) 15%, transparent);
  color: var(--queued);
}

.memory-kind--added {
  background: color-mix(in srgb, var(--running) 15%, transparent);
  color: var(--running);
}
</style>
