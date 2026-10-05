<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { ArrowUp, ChevronLeft, ChevronRight, LoaderCircle } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import PhoneChipSheet, { type ChipOption } from "@/components/phone/session/PhoneChipSheet.vue";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import { useHarnessCatalog } from "@/composables/use-harness-catalog";
import { useNewSessionDefaults } from "@/composables/use-new-session-defaults";
import { useRepositories } from "@/composables/use-repositories";
import { useCreateSession } from "@/composables/use-session-actions";
import { keepOffered, modelFor, modelFromKey, modelName } from "@/lib/agent-model-choice";
import { rememberPhoneMachine } from "@/lib/machines";
import { buildCreateSessionRequest, type NewSessionFolder, type NewSessionWorkspace } from "@/lib/new-session-request";
import { folderFromId, folderId, folderName, folderOptions, type PhoneMachine } from "@/lib/phone/new-session";

/**
 * The New session form for one machine (the page provides it, and builds this again when the machine changes). What
 * to do comes first; under it the choices, each a row that opens a sheet: machine, folder, where in a repository
 * (a new worktree or the folder itself), harness, agent and model. They start where this phone last left them on
 * that machine, like the desktop's. Start creates the session with the message as its first prompt and opens it.
 */
const props = defineProps<{ machine: PhoneMachine; machines: readonly PhoneMachine[] }>();
const emit = defineEmits<{ (event: "machine", id: string): void }>();

type Sheet = "machine" | "folder" | "where" | "harness" | "agent" | "model";

const router = useRouter();
const defaults = useNewSessionDefaults();
const { repositories, isLoading: loadingFolders } = useRepositories();
const { enabledHarnesses, defaultHarnessType, noHarnessReason } = useEnabledHarnesses();
const { createSession, isLoading: starting } = useCreateSession();

const message = shallowRef("");
const folder = shallowRef<NewSessionFolder | null>(null);
const workspace = shallowRef<NewSessionWorkspace>({ kind: "new" });
const harnessType = shallowRef("");
const agent = shallowRef("");
const model = shallowRef("");
const sheet = shallowRef<Sheet | null>(null);
const error = shallowRef<string | null>(null);

// The last folder used on this machine, once the machine's repositories are known.
watch(repositories, (list) => {
  if (folder.value === null) {
    const initial = defaults.initialFolder(list);
    if (initial) pickFolder(initial);
  }
}, { immediate: true });

// The default harness, or the first one that's on when the default isn't.
watch([enabledHarnesses, defaultHarnessType], ([enabled, preferred]) => {
  if (enabled.some((harness) => harness.type === harnessType.value)) return;
  harnessType.value = enabled.find((harness) => harness.type === preferred)?.type ?? enabled[0]?.type ?? "";
}, { immediate: true });

const catalogDirectory = computed(() => (folder.value && folder.value.kind !== "none" ? folder.value.path : null));
const { catalog, agents, models, isSupported } = useHarnessCatalog(harnessType, catalogDirectory);

// Agent and model start as they were last time in this folder on this harness, as long as the harness still offers them.
watch([folder, harnessType, catalog], ([nextFolder, type, nextCatalog], previous) => {
  const changedChoice = !previous || previous[0] !== nextFolder || previous[1] !== type;
  if (!nextFolder || !type) return;
  const remembered = changedChoice ? defaults.choiceFor(nextFolder, type) : { agent: agent.value, model: model.value };
  const kept = nextCatalog ? keepOffered(remembered, nextCatalog) : remembered;
  agent.value = kept.agent;
  model.value = kept.model;
}, { immediate: true });

function pickFolder(next: NewSessionFolder): void {
  folder.value = next;
  workspace.value = next.kind === "repository" ? defaults.workspaceFor(next.path, null) : { kind: "current" };
}

const folderLabel = computed(() => (folder.value ? folderName(folder.value, repositories.value) : loadingFolders.value ? "Loading…" : "Choose"));
const whereLabel = computed(() => {
  const chosen = workspace.value;
  if (chosen.kind === "new") return "New worktree";
  if (chosen.kind === "current") return "The folder itself";
  return chosen.path.split(/[\\/]/).filter(Boolean).pop() ?? chosen.path;
});
const harnessLabel = computed(() => enabledHarnesses.value.find((harness) => harness.type === harnessType.value)?.displayName ?? "None");
const defaultModel = computed(() => (catalog.value ? modelFor(catalog.value, agent.value) : null));
const agentLabel = computed(() => agent.value || "Default");
const modelLabel = computed(() => {
  const chosen = modelFromKey(model.value);
  if (chosen && catalog.value) return modelName(catalog.value, chosen);
  return "Default";
});

const options = computed<ChipOption[]>(() => {
  switch (sheet.value) {
    case "machine":
      return props.machines.map((machine) => ({ id: machine.id, label: machine.name, ...(machine.connection ? {} : { detail: "Home" }) }));
    case "folder":
      return folderOptions(repositories.value, defaults.recentFolders(repositories.value));
    case "where": {
      const list: ChipOption[] = [
        { id: "new", label: "New worktree", detail: "Its own branch and folder; your checkout stays as it is" },
        { id: "current", label: "The folder itself", detail: "Works in your checkout, on its current branch" },
      ];
      if (workspace.value.kind === "existing") list.push({ id: `existing:${workspace.value.path}`, label: whereLabel.value, detail: workspace.value.path });
      return list;
    }
    case "harness":
      return enabledHarnesses.value.map((harness) => ({ id: harness.type, label: harness.displayName }));
    case "agent":
      return [
        { id: "", label: "Default", ...(catalog.value?.defaultAgent ? { detail: catalog.value.defaultAgent } : {}) },
        ...agents.value.map((option) => ({ id: option.id, label: option.name, ...(option.description ? { detail: option.description } : {}) })),
      ];
    case "model":
      return [
        { id: "", label: "Default", ...(defaultModel.value && catalog.value ? { detail: modelName(catalog.value, defaultModel.value) } : {}) },
        ...models.value.map((option) => ({ id: option.selectionKey, label: option.name, detail: option.provider })),
      ];
    default:
      return [];
  }
});

const selectedOption = computed(() => {
  switch (sheet.value) {
    case "machine":
      return props.machine.id;
    case "folder":
      return folder.value ? folderId(folder.value) : "";
    case "where":
      return workspace.value.kind === "existing" ? `existing:${workspace.value.path}` : workspace.value.kind;
    case "harness":
      return harnessType.value;
    case "agent":
      return agent.value;
    case "model":
      return model.value;
    default:
      return "";
  }
});

const sheetTitle: Record<Sheet, string> = {
  machine: "Machine",
  folder: "Folder",
  where: "Where",
  harness: "Harness",
  agent: "Agent",
  model: "Model",
};

function pick(id: string): void {
  error.value = null;
  switch (sheet.value) {
    case "machine":
      if (id !== props.machine.id) emit("machine", id);
      break;
    case "folder":
      pickFolder(folderFromId(id));
      break;
    case "where":
      workspace.value = id === "new" || id === "current" ? { kind: id } : { kind: "existing", path: id.slice("existing:".length) };
      break;
    case "harness":
      harnessType.value = id;
      break;
    case "agent":
      agent.value = id;
      break;
    case "model":
      model.value = id;
      break;
  }
}

const canStart = computed(() => message.value.trim().length > 0 && !!harnessType.value && !starting.value);

async function start(): Promise<void> {
  error.value = null;
  if (!folder.value) {
    sheet.value = "folder";
    return;
  }
  if (!canStart.value) return;

  const built = buildCreateSessionRequest({
    folder: folder.value,
    workspace: workspace.value,
    message: message.value,
    harnessType: harnessType.value,
    ...(agent.value ? { agent: agent.value } : {}),
    model: modelFromKey(model.value),
  });
  if (!built.ok) {
    error.value = built.error;
    return;
  }

  try {
    const created = await createSession(built.directory, built.options);
    defaults.remember(folder.value, workspace.value, { harnessType: harnessType.value, agent: agent.value, model: model.value });
    open(created.session.id);
  } catch (startError) {
    error.value = startError instanceof Error ? startError.message : String(startError);
  }
}

function open(sessionId: string): void {
  const { machine } = props;
  if (!machine.connection) {
    void router.navigate({ to: "/phone/s/$machineId/$sessionId", params: { machineId: machine.id, sessionId } });
    return;
  }
  // Another machine: the page reloads to work there, as opening one from the inbox does.
  rememberPhoneMachine(machine.connection);
  window.location.assign(`/phone/s/${encodeURIComponent(machine.id)}/${encodeURIComponent(sessionId)}`);
}

function back(): void {
  if (window.history.length > 1) window.history.back();
  else void router.navigate({ to: "/phone" });
}
</script>

<template>
  <div
    class="pns"
    data-testid="phone-new-session"
  >
    <header class="pns__head">
      <Button
        variant="toolbar-icon"
        size="icon"
        aria-label="Back"
        @click="back"
      >
        <ChevronLeft :size="22" />
      </Button>
      <h1 class="pns__title">
        New session
      </h1>
    </header>

    <main class="pns__scroll">
      <textarea
        v-model="message"
        class="pns__message phone-composer-input"
        rows="5"
        placeholder="What should the agent do?"
        aria-label="What should the agent do?"
        enterkeyhint="enter"
        data-testid="phone-new-message"
      />

      <div class="pns__card">
        <button
          v-if="machines.length > 1"
          type="button"
          class="pns__row"
          data-testid="phone-new-machine"
          @click="sheet = 'machine'"
        >
          <span class="pns__label">Machine</span>
          <span class="pns__value">{{ machine.name }}</span>
          <ChevronRight
            :size="16"
            aria-hidden="true"
          />
        </button>
        <button
          type="button"
          class="pns__row"
          data-testid="phone-new-folder"
          @click="sheet = 'folder'"
        >
          <span class="pns__label">Folder</span>
          <span
            class="pns__value"
            :class="{ 'pns__value--empty': !folder }"
          >{{ folderLabel }}</span>
          <ChevronRight
            :size="16"
            aria-hidden="true"
          />
        </button>
        <button
          v-if="folder?.kind === 'repository'"
          type="button"
          class="pns__row"
          data-testid="phone-new-where"
          @click="sheet = 'where'"
        >
          <span class="pns__label">Where</span>
          <span class="pns__value">{{ whereLabel }}</span>
          <ChevronRight
            :size="16"
            aria-hidden="true"
          />
        </button>
        <button
          v-if="enabledHarnesses.length > 1"
          type="button"
          class="pns__row"
          data-testid="phone-new-harness"
          @click="sheet = 'harness'"
        >
          <span class="pns__label">Harness</span>
          <span class="pns__value">{{ harnessLabel }}</span>
          <ChevronRight
            :size="16"
            aria-hidden="true"
          />
        </button>
        <template v-if="isSupported">
          <button
            type="button"
            class="pns__row"
            data-testid="phone-new-agent"
            @click="sheet = 'agent'"
          >
            <span class="pns__label">Agent</span>
            <span class="pns__value">{{ agentLabel }}</span>
            <ChevronRight
              :size="16"
              aria-hidden="true"
            />
          </button>
          <button
            type="button"
            class="pns__row"
            data-testid="phone-new-model"
            @click="sheet = 'model'"
          >
            <span class="pns__label">Model</span>
            <span class="pns__value">{{ modelLabel }}</span>
            <ChevronRight
              :size="16"
              aria-hidden="true"
            />
          </button>
        </template>
      </div>

      <p
        v-if="noHarnessReason"
        class="pns__note"
      >
        {{ noHarnessReason }} Turn a harness on in Settings › Harnesses on a computer.
      </p>
      <p
        v-if="error"
        class="pns__error"
        role="alert"
        data-testid="phone-new-error"
      >
        {{ error }}
      </p>
    </main>

    <footer class="pns__foot">
      <Button
        class="h-12 w-full"
        :disabled="!canStart"
        data-testid="phone-new-start"
        @click="start"
      >
        <LoaderCircle
          v-if="starting"
          class="animate-spin"
          aria-hidden="true"
        />
        <ArrowUp
          v-else
          aria-hidden="true"
        />
        Start on {{ machine.name }}
      </Button>
    </footer>

    <PhoneChipSheet
      :open="sheet !== null"
      :title="sheet ? sheetTitle[sheet] : ''"
      :options="options"
      :selected="selectedOption"
      @pick="pick"
      @close="sheet = null"
    />
  </div>
</template>

<style scoped>
.pns {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  height: 100dvh;
}

.pns__head {
  display: flex;
  flex: none;
  align-items: center;
  gap: 4px;
  min-height: 52px;
  padding: 4px 16px 4px 4px;
  border-bottom: 1px solid var(--border);
}

.pns__title {
  font-size: 17px;
  font-weight: 600;
}

.pns__scroll {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 12px;
  min-height: 0;
  overflow-y: auto;
  padding: 12px;
}

.pns__message {
  width: 100%;
  min-height: 132px;
  padding: 12px 14px;
  border: 1px solid var(--border);
  border-radius: var(--radius-panel);
  background: var(--card-bg);
  color: var(--text);
  font: inherit;
  font-size: 16px;
  line-height: 1.45;
  resize: vertical;
  outline: none;
}

.pns__message:focus {
  border-color: color-mix(in srgb, var(--accent) 50%, var(--border));
}

.pns__card {
  border: 1px solid var(--border);
  border-radius: var(--radius-panel);
  background: var(--card-bg);
}

.pns__row {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  min-height: 48px;
  padding: 0 12px 0 14px;
  border: 0;
  border-top: 1px solid var(--border);
  background: transparent;
  color: var(--muted);
  font: inherit;
  font-size: 15px;
  text-align: left;
}

.pns__row:first-child {
  border-top: 0;
}

.pns__label {
  flex: none;
  color: var(--text);
}

.pns__value {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-align: right;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.pns__value--empty {
  color: var(--accent);
}

.pns__note {
  padding: 0 2px;
  font-size: 13px;
  color: var(--muted);
}

.pns__error {
  padding: 0 2px;
  font-size: 13px;
  color: var(--error);
}

.pns__foot {
  flex: none;
  padding: 8px 12px calc(env(safe-area-inset-bottom) + 10px);
  border-top: 1px solid var(--border);
  background: var(--main-bg);
}
</style>
