<script setup lang="ts">
import { computed, onMounted, shallowRef, watch } from "vue";
import { Check, LoaderCircle, Play, Plus, RefreshCw, X } from "lucide-vue-next";
import type { WeaveFlavor, WeaveHarnessCheck, WeaveHarnessDetection } from "@/api/client";
import { Button } from "@/components/ui/button";
import ProfileConfigEditor from "@/components/settings/ProfileConfigEditor.vue";
import { useWeaveConfig } from "@/composables/use-weave-config";

const CONFIG = "config.weave";
const LEGACY_CONFIG = "weave-opencode.jsonc";
const PROMPTS = "prompts/";

const FLAVORS: Record<WeaveFlavor, {
  name: string;
  file: string;
  ownPath: string;
  variable: string;
  projectPath: string;
  minimum: string;
  starter: string;
}> = {
  weave: {
    name: "Weave",
    file: CONFIG,
    ownPath: "~/.weave/config.weave",
    variable: "WEAVE_GLOBAL_CONFIG_DIR",
    projectPath: "<repo>/.weave/config.weave",
    minimum: "0.2.0-next.1",
    starter: "# Fleet's Weave config. Weave reads it in place of ~/.weave/config.weave in Fleet sessions.\n"
      + "# A repo's own .weave/config.weave still applies on top.\n",
  },
  legacy: {
    name: "Weave Legacy",
    file: LEGACY_CONFIG,
    ownPath: "~/.config/opencode/weave-opencode.jsonc",
    variable: "WEAVE_OPENCODE_CONFIG_DIR",
    projectPath: "<repo>/.opencode/weave-opencode.jsonc",
    minimum: "0.9.0",
    starter: "{\n  // Fleet's Weave Legacy config. Weave Legacy reads it in place of\n"
      + "  // ~/.config/opencode/weave-opencode.jsonc in Fleet sessions.\n"
      + "  // A repo's own .opencode/weave-opencode.jsonc still applies on top.\n}\n",
  },
};

const { view, loading, error, load, check, save, readOwn, changePlugin } = useWeaveConfig();

const source = shallowRef<"own" | "fleet">("own");
const files = shallowRef<Record<string, string>>({});
const activeFile = shallowRef(CONFIG);
const busy = shallowRef<"test" | "save" | "copy" | null>(null);
const checks = shallowRef<WeaveHarnessCheck[] | null>(null);
const notice = shallowRef<string | null>(null);
const addingPrompt = shallowRef(false);
const newPromptName = shallowRef("");
/** The harness whose plugin list Fleet is changing (Add Weave or Remove), and what the last change said. */
const pluginBusy = shallowRef<string | null>(null);
const pluginNotice = shallowRef<{ text: string; tone: "ok" | "warn" | "error" } | null>(null);

onMounted(() => load());

/** The draft differs from what's saved. */
const dirty = computed(() => view.value !== null && (
  source.value !== view.value.source
  || JSON.stringify(sortedEntries(files.value)) !== JSON.stringify(sortedEntries(view.value.files))
));

function sortedEntries(record: Readonly<Record<string, string>>): [string, string][] {
  return Object.entries(record).sort(([a], [b]) => a.localeCompare(b));
}

/** OpenCode 2 first: it's the OpenCode Fleet leads with. */
const HARNESS_ORDER = ["opencode2", "opencode"];

const harnesses = computed<readonly WeaveHarnessDetection[]>(() => [...view.value?.harnesses ?? []].sort((a, b) =>
  rank(a.harnessType) - rank(b.harnessType)));

function rank(harnessType: string): number {
  const index = HARNESS_ORDER.indexOf(harnessType);
  return index < 0 ? HARNESS_ORDER.length : index;
}
const installs = computed(() => harnesses.value.flatMap((harness) => harness.installs));
/** The Weaves some harness loads, in the order they're shown. */
const flavors = computed<WeaveFlavor[]>(() =>
  (["weave", "legacy"] as const).filter((flavor) => installs.value.some((install) => install.flavor === flavor)));
/** The Weaves Fleet can hand a config to: at least one harness loads a version that reads Fleet's folder. */
const usableFlavors = computed<WeaveFlavor[]>(() =>
  flavors.value.filter((flavor) => installs.value.some((install) => install.flavor === flavor && install.acceptsFleetConfig)));
const outdated = computed(() => flavors.value.filter((flavor) => !usableFlavors.value.includes(flavor)));
const bothLoaded = computed(() => harnesses.value.some((harness) =>
  harness.installs.some((install) => install.flavor === "weave") && harness.installs.some((install) => install.flavor === "legacy")));

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

function describeInstall(detection: WeaveHarnessDetection): string {
  if (!detection.checked) return detection.note ?? "";
  if (detection.installs.length === 0) {
    return detection.addTo
      ? `Weave isn't in its plugin list. Add Weave puts it in ${detection.addTo}.`
      : "Weave isn't in its plugin list.";
  }
  return detection.installs.map((install) => {
    const entry = install.entry.startsWith("file://")
      ? `${install.package} · local build at ${install.entry.slice("file://".length)}`
      : install.entry;
    return install.error ? `${entry} · didn't load: ${install.error}` : entry;
  }).join(" · ");
}

function pillFor(detection: WeaveHarnessDetection): { tone: "ok" | "warn" | "off"; label: string } {
  if (!detection.checked) return { tone: "off", label: "Not yet" };
  if (detection.installs.length === 0) return { tone: "off", label: "No Weave" };
  const names = detection.installs.map((install) => FLAVORS[install.flavor].name).join(" + ");
  if (detection.installs.some((install) => install.error)) return { tone: "warn", label: `${names} · didn't load` };
  return detection.installs.every((install) => install.acceptsFleetConfig)
    ? { tone: "ok", label: names }
    : { tone: "warn", label: `${names} · update` };
}

/** Fleet can take Weave out of this harness: it's the entry Add Weave put in. */
function addedByFleet(detection: WeaveHarnessDetection): boolean {
  return detection.installs.some((install) => install.addedByFleet);
}

async function changeWeavePlugin(detection: WeaveHarnessDetection, add: boolean): Promise<void> {
  pluginBusy.value = detection.harnessType;
  pluginNotice.value = null;
  try {
    const result = await changePlugin(detection.harnessType, add);
    pluginNotice.value = { text: result.message, tone: result.loaded ? "ok" : "warn" };
  } catch (caught) {
    pluginNotice.value = {
      text: caught instanceof Error ? caught.message : `Couldn't change ${detection.harnessName}'s plugins.`,
      tone: "error",
    };
  } finally {
    pluginBusy.value = null;
  }
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
  <section
    class="min-w-0 rounded-card border border-border bg-card-bg p-6 shadow-sm"
    aria-labelledby="weave-heading"
    data-testid="weave-section"
  >
    <div class="flex flex-wrap items-start justify-between gap-3">
      <div class="flex flex-col gap-1">
        <h2
          id="weave-heading"
          class="text-lg font-semibold text-text"
        >
          Weave
        </h2>
        <p class="max-w-2xl text-sm text-muted">
          Agents, prompts and workflows that Weave adds to your harnesses. Fleet asks each harness which plugins it
          loaded.
        </p>
      </div>
      <Button
        variant="ghost"
        size="sm"
        data-testid="weave-recheck"
        :disabled="loading"
        @click="load(true)"
      >
        <LoaderCircle
          v-if="loading"
          class="animate-spin"
          aria-hidden="true"
        />
        <RefreshCw
          v-else
          aria-hidden="true"
        />
        Check again
      </Button>
    </div>

    <p
      v-if="error"
      class="mt-4 text-sm text-error"
      role="alert"
    >
      {{ error }}
    </p>

    <div
      v-else-if="!view"
      class="mt-5 flex items-center gap-2 text-sm text-muted"
    >
      <LoaderCircle
        :size="16"
        class="animate-spin"
        aria-hidden="true"
      />
      <span>Asking your harnesses which plugins they load…</span>
    </div>

    <template v-else>
      <ul
        class="mt-5 divide-y divide-border overflow-hidden rounded-btn border border-border"
        data-testid="weave-harnesses"
      >
        <li
          v-for="harness in harnesses"
          :key="harness.harnessType"
          class="grid grid-cols-[minmax(0,1fr)_auto] items-center gap-x-3 gap-y-1 bg-main-bg px-3.5 py-2.5 sm:grid-cols-[8rem_minmax(0,1fr)_auto]"
        >
          <span class="text-sm font-medium text-text">{{ harness.harnessName }}</span>
          <span class="col-span-2 row-start-2 min-w-0 break-words text-xs text-muted sm:col-span-1 sm:row-start-auto">
            {{ describeInstall(harness) }}
          </span>
          <span class="flex items-center justify-end gap-2">
            <Button
              v-if="harness.addTo"
              variant="outline"
              size="sm"
              :data-testid="`weave-add-${harness.harnessType}`"
              :disabled="pluginBusy !== null"
              @click="changeWeavePlugin(harness, true)"
            >
              <LoaderCircle
                v-if="pluginBusy === harness.harnessType"
                class="animate-spin"
                aria-hidden="true"
              />
              <Plus
                v-else
                aria-hidden="true"
              />
              {{ pluginBusy === harness.harnessType ? "Adding…" : "Add Weave" }}
            </Button>
            <Button
              v-else-if="addedByFleet(harness)"
              variant="ghost"
              size="sm"
              :data-testid="`weave-remove-${harness.harnessType}`"
              :disabled="pluginBusy !== null"
              @click="changeWeavePlugin(harness, false)"
            >
              <LoaderCircle
                v-if="pluginBusy === harness.harnessType"
                class="animate-spin"
                aria-hidden="true"
              />
              {{ pluginBusy === harness.harnessType ? "Removing…" : "Remove" }}
            </Button>
            <span
              class="inline-flex items-center gap-1.5 whitespace-nowrap rounded-full border px-2 py-0.5 text-[11px] font-medium"
              :class="{
                'border-running/35 bg-running/10 text-running': pillFor(harness).tone === 'ok',
                'border-idle/35 bg-idle/10 text-idle': pillFor(harness).tone === 'warn',
                'border-border text-muted': pillFor(harness).tone === 'off',
              }"
            >
              <span
                class="size-1.5 rounded-full bg-current"
                aria-hidden="true"
              />
              {{ pillFor(harness).label }}
            </span>
          </span>
        </li>
      </ul>

      <p
        v-if="pluginBusy"
        class="mt-2 text-xs text-muted"
        role="status"
        data-testid="weave-plugin-busy"
      >
        The first time, the harness downloads Weave from npm, which can take a minute.
      </p>
      <p
        v-else-if="pluginNotice"
        class="mt-2 break-words text-xs"
        :class="{
          'text-muted': pluginNotice.tone === 'ok',
          'text-idle': pluginNotice.tone === 'warn',
          'text-error': pluginNotice.tone === 'error',
        }"
        :role="pluginNotice.tone === 'error' ? 'alert' : 'status'"
        data-testid="weave-plugin-notice"
      >
        {{ pluginNotice.text }}
      </p>

      <div
        v-for="flavor in outdated"
        :key="flavor"
        class="mt-3 rounded-btn border border-idle/35 bg-idle/10 px-3 py-2.5 text-xs text-text"
        data-testid="weave-outdated"
      >
        <p class="font-semibold text-idle">
          Update {{ FLAVORS[flavor].name }} to {{ FLAVORS[flavor].minimum }} to use a config kept in Fleet
        </p>
        <p class="mt-1">
          This version only reads <code class="font-mono">{{ FLAVORS[flavor].ownPath }}</code>. Update it in your
          opencode.json plugin list, then check again.
        </p>
      </div>

      <p
        v-if="bothLoaded"
        class="mt-3 rounded-btn border border-idle/35 bg-idle/10 px-3 py-2.5 text-xs text-text"
      >
        OpenCode loads both Weave and Weave Legacy, and both add agents such as Loom. Keep one of them in your plugin
        list.
      </p>

      <div class="mt-6 border-t border-border pt-5">
        <h3 class="text-base font-semibold text-text">
          Config
        </h3>
        <p class="mt-0.5 text-sm text-muted">
          Whose config Weave reads in Fleet sessions.
        </p>

        <div
          v-if="flavors.length === 0"
          class="mt-4 grid justify-items-center gap-1.5 rounded-card border border-dashed border-border px-4 py-7 text-center"
          data-testid="weave-none"
        >
          <p class="text-sm font-semibold text-text">
            Weave isn't running in any harness
          </p>
          <p class="max-w-md text-sm text-muted">
            Nothing here changes how Fleet works. Sessions use the harness's own agents. Once Weave is in a harness's
            plugin list, you can set its config here.
            <template v-if="harnesses.some((harness) => harness.addTo)">
              Add Weave above puts it there for you.
            </template>
          </p>
          <a
            class="text-sm text-accent hover:underline"
            href="https://tryweave.io/docs/quickstart/"
            target="_blank"
            rel="noopener"
          >What Weave adds</a>
        </div>

        <template v-else>
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
      </div>
    </template>
  </section>
</template>
