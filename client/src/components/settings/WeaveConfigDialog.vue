<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { Check, LoaderCircle, Play, Plus, X } from "lucide-vue-next";
import type { WeaveFlavor, WeaveHarnessCheck, WeaveHarnessDetection } from "@/api/client";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import ProfileConfigEditor from "@/components/settings/ProfileConfigEditor.vue";
import type { useWeaveConfig } from "@/composables/use-weave-config";
import {
  WEAVE_CONFIG as CONFIG,
  WEAVE_FLAVORS as FLAVORS,
  WEAVE_LEGACY_CONFIG as LEGACY_CONFIG,
  WEAVE_PROMPTS as PROMPTS,
  loadedFlavors,
  usableFlavorsOf,
} from "@/lib/weave-flavors";

/**
 * The Fleet-kept Weave config, in a dialog: whose config Weave reads in Fleet sessions (the user's own file or one kept
 * in Fleet), the files, Test and Save. The config is Fleet's, not a harness's, so every harness's Weave group opens the
 * same one. The draft stays while the dialog is closed.
 */
const props = defineProps<{ weave: ReturnType<typeof useWeaveConfig> }>();
const open = defineModel<boolean>("open", { default: false });

const { view, check, save, readOwn } = props.weave;

const source = shallowRef<"own" | "fleet">("own");
const files = shallowRef<Record<string, string>>({});
const activeFile = shallowRef(CONFIG);
const busy = shallowRef<"test" | "save" | "copy" | null>(null);
const checks = shallowRef<WeaveHarnessCheck[] | null>(null);
const notice = shallowRef<string | null>(null);
const addingPrompt = shallowRef(false);
const newPromptName = shallowRef("");

/** The draft differs from what's saved. */
const dirty = computed(() => view.value !== null && (
  source.value !== view.value.source
  || JSON.stringify(sortedEntries(files.value)) !== JSON.stringify(sortedEntries(view.value.files))
));

function sortedEntries(record: Readonly<Record<string, string>>): [string, string][] {
  return Object.entries(record).sort(([a], [b]) => a.localeCompare(b));
}

/** In the harnesses' own order, as the server sends them. */
const harnesses = computed<readonly WeaveHarnessDetection[]>(() => view.value?.harnesses ?? []);
const flavors = computed(() => loadedFlavors(harnesses.value));
const usableFlavors = computed(() => usableFlavorsOf(harnesses.value));
const outdated = computed(() => flavors.value.filter((flavor) => !usableFlavors.value.includes(flavor)));

/** The files of the Weaves this machine runs. Files of one it doesn't stay saved, out of the way. */
const fileTabs = computed(() => {
  const paths = Object.keys(files.value);
  const weave = flavors.value.includes("weave");
  const legacy = flavors.value.includes("legacy");
  return [
    ...(weave && paths.includes(CONFIG) ? [CONFIG] : []),
    ...(weave ? paths.filter((path) => path.startsWith(PROMPTS)).sort() : []),
    ...(legacy && paths.includes(LEGACY_CONFIG) ? [LEGACY_CONFIG] : []),
  ];
});

watch(fileTabs, (tabs) => {
  if (!tabs.includes(activeFile.value)) activeFile.value = tabs[0] ?? CONFIG;
});

const activeContent = computed({
  get: () => files.value[activeFile.value] ?? "",
  set: (content: string) => {
    files.value = { ...files.value, [activeFile.value]: content };
    checks.value = null;
  },
});

/** Every Weave Fleet can hand a config to gets its file, so the person has somewhere to start. */
function fillStarters(): void {
  const next = { ...files.value };
  for (const flavor of usableFlavors.value) {
    const file = FLAVORS[flavor].file;
    if (next[file] === undefined) next[file] = FLAVORS[flavor].starter;
  }
  files.value = next;
}

// A load (or the view being there already) fills the draft, unless the person has changes they haven't saved.
watch(view, (next, previous) => {
  if (!next || (previous && dirty.value)) return;
  source.value = next.source;
  files.value = { ...next.files };
  if (next.source === "fleet") fillStarters();
}, { immediate: true });

function chooseSource(next: "own" | "fleet"): void {
  if (next === "fleet" && usableFlavors.value.length === 0) return;
  source.value = next;
  checks.value = null;
  notice.value = null;
  if (next === "fleet") fillStarters();
}

async function copyOwn(): Promise<void> {
  busy.value = "copy";
  notice.value = null;
  try {
    const next = { ...files.value };
    const read: string[] = [];
    for (const flavor of usableFlavors.value) {
      const own = await readOwn(flavor);
      if (Object.keys(own.files).length === 0) continue;
      // The flavor's files are replaced as a set: config and, for Weave, its prompts.
      for (const path of Object.keys(next)) {
        if (flavor === "weave" ? path === CONFIG || path.startsWith(PROMPTS) : path === LEGACY_CONFIG) delete next[path];
      }
      Object.assign(next, own.files);
      read.push(own.path);
    }
    files.value = next;
    source.value = "fleet";
    fillStarters();
    checks.value = null;
    notice.value = read.length > 0
      ? `Copied from ${read.join(" and ")}. Not saved yet.`
      : "Your own Weave files are empty, so there was nothing to copy.";
  } catch (caught) {
    notice.value = caught instanceof Error ? caught.message : "Couldn't read your own Weave files.";
  } finally {
    busy.value = null;
  }
}

async function test(): Promise<void> {
  busy.value = "test";
  notice.value = null;
  try {
    const results: WeaveHarnessCheck[] = [];
    for (const flavor of usableFlavors.value) {
      if (files.value[FLAVORS[flavor].file] !== undefined) results.push(...await check(flavor, files.value));
    }
    checks.value = results;
  } catch (caught) {
    notice.value = caught instanceof Error ? caught.message : "Couldn't try this config.";
  } finally {
    busy.value = null;
  }
}

async function saveConfig(): Promise<void> {
  busy.value = "save";
  notice.value = null;
  try {
    const result = await save(source.value, files.value);
    checks.value = result.checks.length > 0 ? result.checks : null;
    if (result.saved) {
      notice.value = source.value === "fleet"
        ? "Saved. New sessions use it, and running ones pick it up as each folder goes idle."
        : "Saved. New sessions read your own Weave files.";
    }
  } catch (caught) {
    notice.value = caught instanceof Error ? caught.message : "Couldn't save the Weave config.";
  } finally {
    busy.value = null;
  }
}

const promptNameProblem = computed(() => {
  const name = newPromptName.value.trim();
  if (!name) return null;
  if (!/^[A-Za-z0-9_-][A-Za-z0-9._-]*$/.test(name)) return "Use letters, digits, '-', '_' and '.'.";
  if (files.value[`${PROMPTS}${promptFileName(name)}`] !== undefined) return "There's already a prompt with that name.";
  return null;
});

function promptFileName(name: string): string {
  const trimmed = name.trim();
  return trimmed.endsWith(".md") ? trimmed : `${trimmed}.md`;
}

function addPrompt(): void {
  const name = newPromptName.value.trim();
  if (!name || promptNameProblem.value) return;
  const path = `${PROMPTS}${promptFileName(name)}`;
  files.value = { ...files.value, [path]: "" };
  activeFile.value = path;
  addingPrompt.value = false;
  newPromptName.value = "";
}

function removePrompt(path: string): void {
  const next = { ...files.value };
  delete next[path];
  files.value = next;
}

const primaryFlavor = computed<WeaveFlavor | null>(() => usableFlavors.value[0] ?? flavors.value[0] ?? null);
const variables = computed(() => usableFlavors.value.map((flavor) => FLAVORS[flavor].variable));
const projectPaths = computed(() => usableFlavors.value.map((flavor) => FLAVORS[flavor].projectPath));
const applyFolders = computed(() => view.value?.apply?.folders ?? []);
const applyDone = computed(() => applyFolders.value.filter((folder) => folder.reloaded).length);

function folderName(directory: string): string {
  return directory.split(/[\\/]/).filter(Boolean).pop() ?? directory;
}
</script>

<template>
  <Dialog v-model:open="open">
    <DialogContent
      class="max-h-[90vh] overflow-y-auto sm:max-w-3xl"
      data-testid="weave-section"
    >
      <DialogHeader>
        <DialogTitle>Weave config</DialogTitle>
        <DialogDescription>Whose config Weave reads in Fleet sessions.</DialogDescription>
      </DialogHeader>
      <template v-if="view && flavors.length > 0">
        <div
          class="mt-4 grid gap-2.5 sm:grid-cols-2"
          role="radiogroup"
          aria-label="Config source"
        >
          <button
            type="button"
            role="radio"
            class="grid gap-1 rounded-btn border px-3.5 py-3 text-left transition-colors"
            :class="source === 'own' ? 'border-accent/55 bg-accent/10' : 'border-border bg-main-bg hover:border-muted/40'"
            :aria-checked="source === 'own'"
            data-testid="weave-source-own"
            @click="chooseSource('own')"
          >
            <span class="text-sm font-semibold text-text">Use my own file</span>
            <span class="break-all font-mono text-xs text-muted">
              {{ flavors.map((flavor) => FLAVORS[flavor].ownPath).join(" · ") }}
            </span>
          </button>
          <button
            type="button"
            role="radio"
            class="grid gap-1 rounded-btn border px-3.5 py-3 text-left transition-colors disabled:cursor-not-allowed disabled:opacity-50"
            :class="source === 'fleet' ? 'border-accent/55 bg-accent/10' : 'border-border bg-main-bg hover:border-muted/40'"
            :aria-checked="source === 'fleet'"
            :disabled="usableFlavors.length === 0"
            data-testid="weave-source-fleet"
            @click="chooseSource('fleet')"
          >
            <span class="text-sm font-semibold text-text">Keep it in Fleet</span>
            <span class="text-xs text-muted">
              {{ usableFlavors.length === 0
                ? `Needs ${outdated.map((flavor) => `${FLAVORS[flavor].name} ${FLAVORS[flavor].minimum}`).join(" or ")} or later`
                : "Edited here and tried before it's used. Your own files stay as they are." }}
            </span>
          </button>
        </div>

        <div
          v-if="source === 'own'"
          class="mt-4 grid gap-2 rounded-btn border border-dashed border-border px-3.5 py-3"
        >
          <p class="text-xs text-muted">
            Weave reads your own files, as it does outside Fleet. Fleet doesn't set anything and doesn't edit them.
          </p>
          <div
            v-if="usableFlavors.length > 0"
            class="flex flex-wrap gap-2"
          >
            <Button
              variant="outline"
              size="sm"
              data-testid="weave-copy-own"
              :disabled="busy !== null"
              @click="copyOwn"
            >
              Copy into Fleet's config
            </Button>
          </div>
        </div>

        <template v-else>
          <div class="mt-4 min-w-0 overflow-hidden rounded-btn border border-border bg-main-bg">
            <div
              class="flex overflow-x-auto border-b border-border"
              role="tablist"
              aria-label="Files"
            >
              <div
                v-for="path in fileTabs"
                :key="path"
                class="flex shrink-0 items-center border-r border-border"
                :class="activeFile === path ? 'bg-card-bg shadow-[inset_0_-2px_0_var(--accent)]' : ''"
              >
                <button
                  type="button"
                  role="tab"
                  class="px-3.5 py-2 font-mono text-[11.5px]"
                  :class="activeFile === path ? 'text-text' : 'text-muted hover:text-text'"
                  :aria-selected="activeFile === path"
                  :data-testid="`weave-tab-${path}`"
                  @click="activeFile = path"
                >
                  {{ path }}
                </button>
                <button
                  v-if="path.startsWith(PROMPTS)"
                  type="button"
                  class="mr-1.5 rounded p-0.5 text-muted hover:text-text"
                  :aria-label="`Remove ${path}`"
                  @click="removePrompt(path)"
                >
                  <X
                    :size="12"
                    aria-hidden="true"
                  />
                </button>
              </div>
              <form
                v-if="usableFlavors.includes('weave') && addingPrompt"
                class="flex shrink-0 items-center gap-1 px-2"
                @submit.prevent="addPrompt"
              >
                <span class="font-mono text-[11.5px] text-muted">prompts/</span>
                <input
                  v-model="newPromptName"
                  class="h-6 w-28 rounded border border-border bg-card-bg px-1.5 font-mono text-[11.5px] text-text outline-none focus:border-accent"
                  placeholder="team.md"
                  aria-label="New prompt file name"
                  data-testid="weave-new-prompt"
                  @keydown.escape="addingPrompt = false"
                >
                <Button
                  type="submit"
                  size="sm"
                  variant="ghost"
                  :disabled="!newPromptName.trim() || promptNameProblem !== null"
                >
                  Add
                </Button>
              </form>
              <button
                v-else-if="usableFlavors.includes('weave')"
                type="button"
                class="flex shrink-0 items-center gap-1 px-3 font-mono text-[11.5px] text-muted hover:text-text"
                data-testid="weave-add-prompt"
                @click="addingPrompt = true"
              >
                <Plus
                  :size="12"
                  aria-hidden="true"
                />
                prompt
              </button>
            </div>
            <ProfileConfigEditor
              :key="activeFile"
              v-model="activeContent"
              class="rounded-none border-0"
              :label="activeFile"
              :filename="activeFile"
              @save="saveConfig"
            />
          </div>
          <p
            v-if="promptNameProblem && addingPrompt"
            class="mt-1.5 text-xs text-idle"
          >
            {{ promptNameProblem }}
          </p>

          <p class="mt-2 max-w-3xl text-xs text-muted">
            Fleet gives this to Weave as
            <template
              v-for="(variable, index) in variables"
              :key="variable"
            >
              <template v-if="index > 0">
                and
              </template>
              <code class="font-mono text-text">{{ variable }}</code>
            </template>
            when it starts OpenCode.
            <template v-if="usableFlavors.includes('weave')">
              Prompt files sit next to it in <code class="font-mono text-text">prompts/</code>, where
              <code class="font-mono text-text">prompt_file</code> and
              <code class="font-mono text-text">prompt_append_file</code> look for them.
            </template>
            Project configs (<code class="font-mono text-text">{{ projectPaths.join(", ") }}</code>) still apply on top.
          </p>
        </template>

        <div
          v-for="result in checks ?? []"
          :key="`${result.harnessType}-${result.flavor}`"
          class="mt-3 grid gap-1.5 rounded-card border px-3 py-2.5 text-xs"
          :class="result.check.ok ? 'border-running/30 bg-running/10' : 'border-error/30 bg-error/10'"
          role="status"
          data-testid="weave-check"
        >
          <p
            class="flex items-center gap-1.5 font-semibold"
            :class="result.check.ok ? 'text-running' : 'text-error'"
          >
            <Check
              v-if="result.check.ok"
              :size="13"
              aria-hidden="true"
            />
            <X
              v-else
              :size="13"
              aria-hidden="true"
            />
            <template v-if="result.check.ok">
              {{ result.harnessName }} loads {{ result.check.agents.length }} {{ FLAVORS[result.flavor].name }}
              agent{{ result.check.agents.length === 1 ? "" : "s" }}
            </template>
            <template v-else>
              {{ result.check.error }}
            </template>
          </p>
          <div
            v-if="result.check.ok"
            class="flex flex-wrap gap-1"
          >
            <span
              v-for="agent in result.check.agents"
              :key="agent"
              class="rounded-full border border-border bg-card-bg px-2 py-px font-mono text-[11px] text-text"
            >{{ agent }}</span>
          </div>
          <pre
            v-else-if="result.check.details?.length"
            class="overflow-x-auto whitespace-pre font-mono text-[11px] text-text"
          >{{ result.check.details.join("\n") }}</pre>
          <p
            v-if="!result.check.ok"
            class="text-text"
          >
            Nothing was saved. Sessions keep using the last saved config.
          </p>
        </div>

        <div
          v-if="applyFolders.length > 0"
          class="mt-3 grid gap-1.5"
          data-testid="weave-apply"
        >
          <p class="text-xs text-muted">
            {{ applyDone === applyFolders.length
              ? `Applied to ${applyFolders.length === 1 ? "the running folder" : `all ${applyFolders.length} running folders`}.`
              : `Applied to ${applyDone} of ${applyFolders.length} running folders. The rest reload when their turn finishes.` }}
          </p>
          <div
            v-for="folder in applyFolders"
            :key="folder.directory"
            class="grid grid-cols-[minmax(0,1fr)_auto] items-center gap-2.5 rounded-btn border border-border bg-main-bg px-3 py-2"
          >
            <span class="min-w-0 text-[12.5px] text-text">
              {{ folderName(folder.directory) }}
              <span class="block truncate font-mono text-[11px] text-muted">{{ folder.directory }}</span>
            </span>
            <span
              class="inline-flex items-center gap-1.5 whitespace-nowrap rounded-full border px-2 py-0.5 text-[11px] font-medium"
              :class="folder.reloaded ? 'border-running/35 bg-running/10 text-running' : 'border-idle/35 bg-idle/10 text-idle'"
            >
              <span
                class="size-1.5 rounded-full bg-current"
                aria-hidden="true"
              />
              {{ folder.reloaded ? "Reloaded" : "Waiting · turn running" }}
            </span>
          </div>
          <p
            v-if="view.apply?.error"
            class="text-xs text-idle"
          >
            {{ view.apply.error }}
          </p>
        </div>

        <p
          v-if="notice"
          class="mt-3 text-xs text-muted"
          role="status"
          data-testid="weave-notice"
        >
          {{ notice }}
        </p>

        <div
          v-if="source === 'fleet' || dirty"
          class="mt-4 flex flex-wrap items-center justify-between gap-2"
        >
          <div class="flex gap-1">
            <Button
              v-if="source === 'fleet' && primaryFlavor"
              variant="ghost"
              size="sm"
              :disabled="busy !== null"
              @click="copyOwn"
            >
              Copy from {{ usableFlavors.map((flavor) => FLAVORS[flavor].ownPath).join(" and ") }}
            </Button>
          </div>
          <div class="flex gap-1">
            <Button
              v-if="source === 'fleet'"
              variant="outline"
              size="sm"
              data-testid="weave-test"
              :disabled="busy !== null"
              @click="test"
            >
              <LoaderCircle
                v-if="busy === 'test'"
                class="animate-spin"
                aria-hidden="true"
              />
              <Play
                v-else
                aria-hidden="true"
              />
              Test
            </Button>
            <Button
              size="sm"
              data-testid="weave-save"
              :disabled="busy !== null || (!dirty && source === view.source)"
              @click="saveConfig"
            >
              <LoaderCircle
                v-if="busy === 'save'"
                class="animate-spin"
                aria-hidden="true"
              />
              {{ busy === "save" && source === "fleet" ? "Checking with OpenCode…" : "Save" }}
            </Button>
          </div>
        </div>
      </template>
    </DialogContent>
  </Dialog>
</template>
