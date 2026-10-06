<script setup lang="ts">
import { computed, nextTick, onMounted, shallowRef, useTemplateRef, watch } from "vue";
import { ArrowUp, Check, ChevronDown, ChevronLeft, Cpu, Folder, GitBranch, LoaderCircle, Monitor, Search, X } from "lucide-vue-next";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import { useHarnessCatalog } from "@/composables/use-harness-catalog";
import { useNewSessionDefaults } from "@/composables/use-new-session-defaults";
import { useRepositories } from "@/composables/use-repositories";
import { useCreateSession } from "@/composables/use-session-actions";
import { keepOffered, modelFor, modelFromKey, modelName } from "@/lib/agent-model-choice";
import { animateTo, EASE_OUT } from "@/lib/phone/animate";
import { haptic } from "@/lib/phone/haptics";
import { takeKeyboard } from "@/lib/phone/keyboard";
import { buildCreateSessionRequest, type NewSessionFolder, type NewSessionWorkspace } from "@/lib/new-session-request";
import { folderFromId, folderId, folderName, folderOptions, startCaption, type PhoneMachine } from "@/lib/phone/new-session";
import { autogrow, grownHeight } from "@/lib/phone/keyboard";

/**
 * The New session sheet's pages for one machine (the sheet provides the machine, and builds this again when it
 * changes), as the desktop's new-session composer: what to do in the composer's frame, the agent and model in its
 * toolbar (shimmering in place while the harness lists them), and under it the Machine, Folder, Where and Harness
 * chips, each opening a picker page that slides in inside the sheet, and a line saying what Start will do. Start,
 * kept above the keyboard, creates the session with the message as its first prompt and opens it.
 */
const props = defineProps<{ machine: PhoneMachine; machines: readonly PhoneMachine[] }>();
const message = defineModel<string>("message", { required: true });
const emit = defineEmits<{ (event: "machine", id: string): void; (event: "cancel"): void; (event: "started", sessionId: string): void }>();

type Picker = "machine" | "folder" | "where" | "harness" | "agent" | "model";
interface Option { id: string; label: string; detail?: string }

const defaults = useNewSessionDefaults();
const { repositories, isLoading: loadingFolders } = useRepositories();
const { enabledHarnesses, defaultHarnessType, noHarnessReason } = useEnabledHarnesses();
const { createSession, isLoading: starting } = useCreateSession();

const folder = shallowRef<NewSessionFolder | null>(null);
const workspace = shallowRef<NewSessionWorkspace>({ kind: "new" });
const harnessType = shallowRef("");
const agent = shallowRef("");
const model = shallowRef("");
const picker = shallowRef<Picker | null>(null);
const folderQuery = shallowRef("");
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
const { catalog, agents, models, isSupported, isCurrent, isLoading: loadingCatalog } = useHarnessCatalog(harnessType, catalogDirectory);
/** Agent and model rows stay in place, shimmering, while the harness lists them (nothing pops in later). */
const catalogLoading = computed(() => Boolean(harnessType.value) && (!isCurrent.value || (loadingCatalog.value && !catalog.value)));
const showAgentModel = computed(() => isSupported.value || catalogLoading.value);

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

const folderLabel = computed(() => (folder.value ? folderName(folder.value, repositories.value) : loadingFolders.value ? "" : "Choose"));
const whereLabel = computed(() => {
  const chosen = workspace.value;
  if (chosen.kind === "new") return "New worktree";
  if (chosen.kind === "current") return "This folder";
  return chosen.path.split(/[\\/]/).filter(Boolean).pop() ?? chosen.path;
});
const harnessLabel = computed(() => enabledHarnesses.value.find((harness) => harness.type === harnessType.value)?.displayName ?? "None");
const caption = computed(() => startCaption({
  machine: props.machine.name,
  folder: folder.value,
  folderName: folder.value ? folderName(folder.value, repositories.value) : "",
  workspace: workspace.value,
  harness: harnessType.value ? harnessLabel.value : null,
}));
const defaultModel = computed(() => (catalog.value ? modelFor(catalog.value, agent.value) : null));
/** What the toolbar names: the choice, else the harness's own default by name, as the desktop composer does. */
const agentLabel = computed(() => agent.value || catalog.value?.defaultAgent || "Default");
const modelLabel = computed(() => {
  const chosen = modelFromKey(model.value) ?? defaultModel.value;
  if (chosen && catalog.value) return modelName(catalog.value, chosen);
  return "Default";
});

const options = computed<Option[]>(() => {
  switch (picker.value) {
    case "machine":
      return props.machines.map((machine) => ({ id: machine.id, label: machine.name, ...(machine.connection ? {} : { detail: "Home: this phone's own machine" }) }));
    case "folder": {
      const words = folderQuery.value.trim().toLowerCase();
      return folderOptions(repositories.value, defaults.recentFolders(repositories.value))
        .filter((option) => !words || option.label.toLowerCase().includes(words) || option.detail?.toLowerCase().includes(words));
    }
    case "where": {
      const name = folder.value ? folderName(folder.value, repositories.value) : "the folder";
      const list: Option[] = [
        { id: "new", label: "New worktree", detail: "Its own branch and folder, so it can't trip over other sessions" },
        { id: "current", label: "This folder", detail: `Works straight in ${name}, on its current branch` },
      ];
      if (workspace.value.kind === "existing") list.push({ id: `existing:${workspace.value.path}`, label: whereLabel.value, detail: workspace.value.path });
      return list;
    }
    case "harness":
      return enabledHarnesses.value.map((harness) => ({ id: harness.type, label: harness.displayName }));
    case "agent":
      return [
        { id: "", label: "Default", ...(catalog.value?.defaultAgent ? { detail: `The harness's own: ${catalog.value.defaultAgent}` } : {}) },
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
  switch (picker.value) {
    case "machine": return props.machine.id;
    case "folder": return folder.value ? folderId(folder.value) : "";
    case "where": return workspace.value.kind === "existing" ? `existing:${workspace.value.path}` : workspace.value.kind;
    case "harness": return harnessType.value;
    case "agent": return agent.value;
    case "model": return model.value;
    default: return "";
  }
});

const pickerTitle: Record<Picker, string> = { machine: "Machine", folder: "Folder", where: "Where", harness: "Harness", agent: "Agent", model: "Model" };

// Picker pages slide in from the right over the form, which slides a little left and dims, and back.
const mainRef = useTemplateRef<HTMLElement>("main");
function openPicker(next: Picker): void {
  error.value = null;
  folderQuery.value = "";
  (document.activeElement as HTMLElement | null)?.blur?.();
  picker.value = next;
}
function closePicker(): void {
  picker.value = null;
}
async function onPickerEnter(el: Element, done: () => void): Promise<void> {
  const page = el as HTMLElement;
  page.style.transform = "translateX(100%)";
  const moves = [animateTo(page, { transform: "translateX(0)" }, 400)];
  if (mainRef.value) moves.push(animateTo(mainRef.value, { transform: "translateX(-30%)", opacity: "0.6" }, 400));
  await Promise.all(moves);
  done();
}
async function onPickerLeave(el: Element, done: () => void): Promise<void> {
  const moves = [animateTo(el as HTMLElement, { transform: "translateX(100%)" }, 340, EASE_OUT)];
  if (mainRef.value) moves.push(animateTo(mainRef.value, { transform: "translateX(0)", opacity: "1" }, 340, EASE_OUT));
  await Promise.all(moves);
  done();
}

function pick(id: string): void {
  switch (picker.value) {
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
  setTimeout(closePicker, 180);
}

const canStart = computed(() => message.value.trim().length > 0 && !!harnessType.value && !starting.value);

async function start(): Promise<void> {
  error.value = null;
  if (!folder.value) {
    openPicker("folder");
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

  haptic("success");
  try {
    const created = await createSession(built.directory, built.options);
    defaults.remember(folder.value, workspace.value, { harnessType: harnessType.value, agent: agent.value, model: model.value });
    emit("started", created.session.id);
  } catch (startError) {
    error.value = startError instanceof Error ? startError.message : String(startError);
  }
}

// The message box grows with what's typed, from four lines.
const promptRef = useTemplateRef<HTMLTextAreaElement>("prompt");
const MIN_HEIGHT = grownHeight(Infinity, 4);
function fit(): void {
  autogrow(promptRef.value, MIN_HEIGHT);
}
watch(message, () => void nextTick(fit));
onMounted(() => {
  fit();
  // The tap that opened the sheet is holding the keyboard: hand it to the message box.
  takeKeyboard(promptRef.value);
});
</script>

<template>
  <div
    ref="main"
    class="ph-sheet-page"
    data-testid="phone-new-session"
  >
    <div class="ph-sheet__head">
      <h2>New session</h2>
      <button
        type="button"
        class="ph-icon-btn"
        aria-label="Close"
        data-testid="phone-new-cancel"
        @click="emit('cancel')"
      >
        <X aria-hidden="true" />
      </button>
    </div>
    <div class="ph-sheet__body">
      <div class="ph-sheet__pad">
        <div class="ph-frame">
          <textarea
            ref="prompt"
            v-model="message"
            class="phone-composer-input pns__prompt"
            rows="4"
            placeholder="Describe the task, or ask a question…"
            aria-label="What should the agent do?"
            enterkeyhint="enter"
            data-testid="phone-new-message"
          />
          <div class="ph-frame__bar pns__bar">
            <template v-if="showAgentModel">
              <button
                type="button"
                class="ph-sel"
                :disabled="catalogLoading"
                data-testid="phone-new-agent"
                @click="openPicker('agent')"
              >
                <span
                  v-if="catalogLoading"
                  class="ph-sk pns__sk"
                />
                <span
                  v-else
                  class="ph-fade-in"
                >{{ agentLabel }}</span>
                <ChevronDown aria-hidden="true" />
              </button>
              <button
                type="button"
                class="ph-sel"
                :disabled="catalogLoading"
                data-testid="phone-new-model"
                @click="openPicker('model')"
              >
                <span
                  v-if="catalogLoading"
                  class="ph-sk pns__sk"
                />
                <span
                  v-else
                  class="ph-fade-in"
                >{{ modelLabel }}</span>
                <ChevronDown aria-hidden="true" />
              </button>
            </template>
          </div>
        </div>

        <div class="ph-chips pns__chips">
          <button
            type="button"
            class="ph-chip"
            :disabled="machines.length < 2"
            data-testid="phone-new-machine"
            @click="openPicker('machine')"
          >
            <Monitor aria-hidden="true" />
            <span>{{ machine.name }}</span>
            <ChevronDown
              v-if="machines.length > 1"
              class="ph-chip__chev"
              aria-hidden="true"
            />
          </button>
          <button
            type="button"
            class="ph-chip"
            :class="{ 'ph-chip--muted': !folder }"
            data-testid="phone-new-folder"
            @click="openPicker('folder')"
          >
            <Folder aria-hidden="true" />
            <span v-if="folderLabel">{{ folder ? folderLabel : "Choose a folder" }}</span>
            <span
              v-else
              class="ph-sk pns__sk"
            />
            <ChevronDown
              class="ph-chip__chev"
              aria-hidden="true"
            />
          </button>
          <button
            v-if="folder?.kind === 'repository'"
            type="button"
            class="ph-chip"
            data-testid="phone-new-where"
            @click="openPicker('where')"
          >
            <GitBranch aria-hidden="true" />
            <span>{{ whereLabel }}</span>
            <ChevronDown
              class="ph-chip__chev"
              aria-hidden="true"
            />
          </button>
          <button
            type="button"
            class="ph-chip"
            :disabled="enabledHarnesses.length < 2"
            data-testid="phone-new-harness"
            @click="openPicker('harness')"
          >
            <Cpu aria-hidden="true" />
            <span>{{ harnessLabel }}</span>
            <ChevronDown
              v-if="enabledHarnesses.length > 1"
              class="ph-chip__chev"
              aria-hidden="true"
            />
          </button>
        </div>
        <p
          v-if="noHarnessReason"
          class="ph-foot pns__caption"
        >
          {{ noHarnessReason }} Turn a harness on in Settings › Harnesses on a computer.
        </p>
        <p
          v-else
          class="ph-foot pns__caption"
          data-testid="phone-new-caption"
        >
          <template
            v-for="(part, index) in caption"
            :key="index"
          >
            <b v-if="part.bold">{{ part.text }}</b><span v-else>{{ part.text }}</span>
          </template>
        </p>
        <p
          v-if="error"
          class="ph-foot ph-foot--bad pns__caption"
          role="alert"
          data-testid="phone-new-error"
        >
          {{ error }}
        </p>
      </div>
    </div>
    <div class="ph-sheet__foot">
      <button
        type="button"
        class="ph-btn ph-btn--primary ph-btn--block"
        :disabled="!canStart"
        data-testid="phone-new-start"
        @click="start"
      >
        <LoaderCircle
          v-if="starting"
          class="ph-spinner"
          aria-hidden="true"
        />
        <ArrowUp
          v-else
          aria-hidden="true"
        />
        <span>{{ starting ? "Starting…" : `Start on ${machine.name}` }}</span>
      </button>
    </div>
  </div>

  <Transition
    :css="false"
    @enter="onPickerEnter"
    @leave="onPickerLeave"
  >
    <div
      v-if="picker"
      :key="picker"
      class="ph-sheet-page"
      data-testid="phone-new-picker"
    >
      <div class="ph-sheet__head ph-sheet__head--back">
        <button
          type="button"
          class="ph-icon-btn ph-icon-btn--text"
          aria-label="Back"
          @click="closePicker"
        >
          <ChevronLeft aria-hidden="true" />
        </button>
        <h2>{{ pickerTitle[picker] }}</h2>
      </div>
      <div class="ph-sheet__body">
        <div
          v-if="picker === 'folder'"
          class="ph-sheet__pad pns__search"
        >
          <label class="ph-field">
            <Search aria-hidden="true" />
            <input
              v-model="folderQuery"
              class="phone-composer-input"
              type="search"
              :placeholder="`Search folders on ${machine.name}`"
              aria-label="Search folders"
              autocapitalize="off"
              autocomplete="off"
              spellcheck="false"
            >
          </label>
        </div>
        <div class="ph-card">
          <button
            v-for="option in options"
            :key="option.id"
            type="button"
            class="ph-set"
            :aria-pressed="option.id === selectedOption"
            @click="pick(option.id)"
          >
            <span class="ph-set__main">
              <span class="ph-set__t">{{ option.label }}</span>
              <span
                v-if="option.detail"
                class="ph-set__s"
              >{{ option.detail }}</span>
            </span>
            <Check
              v-if="option.id === selectedOption"
              class="ph-set__check"
              aria-hidden="true"
            />
            <span
              v-else
              class="ph-set__nocheck"
            />
          </button>
        </div>
        <p
          v-if="picker === 'folder' && loadingFolders && options.length <= 1"
          class="ph-foot"
        >
          Looking for folders on {{ machine.name }}…
        </p>
      </div>
    </div>
  </Transition>
</template>

<style scoped>
.pns__prompt {
  max-height: 40vh;
}

.pns__bar {
  min-height: 42px;
}

.pns__sk {
  width: 44px;
}

.pns__chips {
  margin-top: 12px;
}

.pns__caption {
  margin: 10px 2px 0;
}

.pns__caption b {
  font-weight: 600;
}

.pns__search {
  margin-bottom: 12px;
}
</style>
