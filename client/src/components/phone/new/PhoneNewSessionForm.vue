<script setup lang="ts">
import { computed, nextTick, onMounted, shallowRef, useTemplateRef, watch } from "vue";
import { ArrowLeft, ArrowUp, Check, ChevronLeft, ChevronRight, LoaderCircle, Search } from "lucide-vue-next";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import { useHarnessCatalog } from "@/composables/use-harness-catalog";
import { useNewSessionDefaults } from "@/composables/use-new-session-defaults";
import { useRepositories } from "@/composables/use-repositories";
import { useCreateSession } from "@/composables/use-session-actions";
import { phoneLook } from "@/composables/phone/use-phone-env";
import { keepOffered, modelFor, modelFromKey, modelName } from "@/lib/agent-model-choice";
import { animateTo, EASE_OUT } from "@/lib/phone/animate";
import { haptic } from "@/lib/phone/haptics";
import { takeKeyboard } from "@/lib/phone/keyboard";
import { buildCreateSessionRequest, type NewSessionFolder, type NewSessionWorkspace } from "@/lib/new-session-request";
import { folderFromId, folderId, folderName, folderOptions, type PhoneMachine } from "@/lib/phone/new-session";

/**
 * The New session sheet's pages for one machine (the sheet provides the machine, and builds this again when it
 * changes). What to do comes first, with the keyboard up; under it the choices as grouped rows (machine, folder,
 * where in a repository, harness, agent and model), each opening a picker page that slides in inside the sheet.
 * Agent and model shimmer in place while the harness lists them. Start, kept above the keyboard, creates the session
 * with the message as its first prompt and opens it.
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
const defaultModel = computed(() => (catalog.value ? modelFor(catalog.value, agent.value) : null));
const agentLabel = computed(() => agent.value || "Default");
const modelLabel = computed(() => {
  const chosen = modelFromKey(model.value);
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
function fit(): void {
  const el = promptRef.value;
  if (!el) return;
  el.style.height = "auto";
  el.style.height = `${Math.max(el.scrollHeight, 22 * 4)}px`;
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
      <button
        type="button"
        class="ph-navbtn ph-glass ph-navbtn--text"
        data-testid="phone-new-cancel"
        @click="emit('cancel')"
      >
        Cancel
      </button>
      <h2>New session</h2>
      <span class="ph-navbar__spacer" />
    </div>
    <div class="ph-sheet__body">
      <div class="ph-sheet__pad">
        <div class="ph-prompt-card">
          <textarea
            ref="prompt"
            v-model="message"
            class="phone-composer-input"
            rows="4"
            placeholder="What should the agent do?"
            aria-label="What should the agent do?"
            enterkeyhint="enter"
            data-testid="phone-new-message"
          />
        </div>
      </div>

      <div class="ph-group-h">
        Where it runs
      </div>
      <div class="ph-group">
        <button
          type="button"
          class="ph-row"
          :class="{ 'ph-row--static': machines.length < 2 }"
          :disabled="machines.length < 2"
          data-testid="phone-new-machine"
          @click="openPicker('machine')"
        >
          <span class="ph-row__main"><span class="ph-row__title">Machine</span></span>
          <span class="ph-row__value">{{ machine.name }}</span>
          <ChevronRight
            v-if="machines.length > 1"
            class="ph-row__chev"
            :size="16"
            :stroke-width="3"
            aria-hidden="true"
          />
        </button>
        <button
          type="button"
          class="ph-row"
          data-testid="phone-new-folder"
          @click="openPicker('folder')"
        >
          <span class="ph-row__main"><span class="ph-row__title">Folder</span></span>
          <span
            v-if="folderLabel"
            class="ph-row__value"
            :class="{ 'pns__choose': !folder }"
          >{{ folderLabel }}</span>
          <span
            v-else
            class="ph-sk pns__sk"
          />
          <ChevronRight
            class="ph-row__chev"
            :size="16"
            :stroke-width="3"
            aria-hidden="true"
          />
        </button>
        <button
          v-if="folder?.kind === 'repository'"
          type="button"
          class="ph-row"
          data-testid="phone-new-where"
          @click="openPicker('where')"
        >
          <span class="ph-row__main"><span class="ph-row__title">Where</span></span>
          <span class="ph-row__value">{{ whereLabel }}</span>
          <ChevronRight
            class="ph-row__chev"
            :size="16"
            :stroke-width="3"
            aria-hidden="true"
          />
        </button>
      </div>

      <div class="ph-group-h">
        Who does it
      </div>
      <div class="ph-group">
        <button
          type="button"
          class="ph-row"
          :class="{ 'ph-row--static': enabledHarnesses.length < 2 }"
          :disabled="enabledHarnesses.length < 2"
          data-testid="phone-new-harness"
          @click="openPicker('harness')"
        >
          <span class="ph-row__main"><span class="ph-row__title">Harness</span></span>
          <span class="ph-row__value">{{ harnessLabel }}</span>
          <ChevronRight
            v-if="enabledHarnesses.length > 1"
            class="ph-row__chev"
            :size="16"
            :stroke-width="3"
            aria-hidden="true"
          />
        </button>
        <template v-if="showAgentModel">
          <button
            type="button"
            class="ph-row"
            :disabled="catalogLoading"
            data-testid="phone-new-agent"
            @click="openPicker('agent')"
          >
            <span class="ph-row__main"><span class="ph-row__title">Agent</span></span>
            <span
              v-if="catalogLoading"
              class="ph-sk pns__sk"
            />
            <span
              v-else
              class="ph-row__value ph-fade-in"
            >{{ agentLabel }}</span>
            <ChevronRight
              class="ph-row__chev"
              :size="16"
              :stroke-width="3"
              aria-hidden="true"
            />
          </button>
          <button
            type="button"
            class="ph-row"
            :disabled="catalogLoading"
            data-testid="phone-new-model"
            @click="openPicker('model')"
          >
            <span class="ph-row__main"><span class="ph-row__title">Model</span></span>
            <span
              v-if="catalogLoading"
              class="ph-sk pns__sk"
            />
            <span
              v-else
              class="ph-row__value ph-fade-in"
            >{{ modelLabel }}</span>
            <ChevronRight
              class="ph-row__chev"
              :size="16"
              :stroke-width="3"
              aria-hidden="true"
            />
          </button>
        </template>
      </div>
      <p
        v-if="noHarnessReason"
        class="ph-group-f"
      >
        {{ noHarnessReason }} Turn a harness on in Settings › Harnesses on a computer.
      </p>
      <p
        v-else-if="showAgentModel"
        class="ph-group-f"
      >
        Agent and model come from {{ harnessLabel }} on {{ machine.name }}.
      </p>
      <p
        v-if="error"
        class="ph-note ph-note--error pns__error"
        role="alert"
        data-testid="phone-new-error"
      >
        {{ error }}
      </p>
    </div>
    <div class="ph-sheet__foot">
      <button
        type="button"
        class="ph-btn ph-btn--primary ph-btn--big"
        :disabled="!canStart"
        data-testid="phone-new-start"
        @click="start"
      >
        <LoaderCircle
          v-if="starting"
          class="ph-spinner"
          :size="20"
          aria-hidden="true"
        />
        <ArrowUp
          v-else
          :size="22"
          :stroke-width="2.6"
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
      <div class="ph-sheet__head">
        <button
          type="button"
          class="ph-navbtn ph-glass"
          aria-label="Back"
          @click="closePicker"
        >
          <ArrowLeft
            v-if="phoneLook === 'android'"
            :size="24"
            aria-hidden="true"
          />
          <ChevronLeft
            v-else
            :size="24"
            :stroke-width="2.4"
            aria-hidden="true"
          />
        </button>
        <h2>{{ pickerTitle[picker] }}</h2>
      </div>
      <div class="ph-sheet__body">
        <div
          v-if="picker === 'folder'"
          class="ph-sheet__pad pns__search"
        >
          <label class="ph-search">
            <Search
              :size="18"
              aria-hidden="true"
            />
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
        <div class="ph-group">
          <button
            v-for="option in options"
            :key="option.id"
            type="button"
            class="ph-row"
            :aria-pressed="option.id === selectedOption"
            @click="pick(option.id)"
          >
            <span class="ph-row__main">
              <span class="ph-row__title">{{ option.label }}</span>
              <span
                v-if="option.detail"
                class="ph-row__sub ph-row__sub--wrap"
              >{{ option.detail }}</span>
            </span>
            <Check
              v-if="option.id === selectedOption"
              class="ph-row__check"
              :size="22"
              :stroke-width="2.6"
              aria-hidden="true"
            />
            <span
              v-else
              class="pns__no-check"
            />
          </button>
        </div>
        <p
          v-if="picker === 'folder' && loadingFolders && options.length <= 1"
          class="ph-group-f"
        >
          Looking for folders on {{ machine.name }}…
        </p>
      </div>
    </div>
  </Transition>
</template>

<style scoped>
.ph-row__title,
.ph-row__sub {
  display: block;
}

.ph-prompt-card textarea {
  min-height: calc(22px * 4);
  max-height: 40vh;
}

.pns__sk {
  width: 64px;
}

.pns__choose {
  color: var(--accent);
}

.pns__no-check {
  width: 22px;
  flex: none;
}

.pns__search {
  margin-bottom: 14px;
}

.pns__error {
  margin: 12px 32px 0;
}
</style>
