<script setup lang="ts">
import { computed, ref, shallowRef, watch } from "vue";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { fetchServerCanvases } from "@/composables/use-server-canvases";
import { browserTarget } from "@/lib/browser-target";
import { useAppRunsStore } from "@/stores/app-runs";
import { serverCanvasTabId, useCanvasesStore } from "@/stores/canvases";

/**
 * The + menu's Browser entry: run a command in the session's folder, or show an address on Fleet's machine, in a
 * new browser tab. The field starts with the command that last served a page in this project.
 */
const props = defineProps<{
  sessionId: string;
  open: boolean;
}>();

const emit = defineEmits<{
  "update:open": [value: boolean];
}>();

const appRuns = useAppRunsStore();
const canvases = useCanvasesStore();

const value = ref("");
const suggestions = shallowRef<string[]>([]);
const busy = ref(false);
const error = ref<string | null>(null);
let touched = false;

const target = computed(() => browserTarget(value.value));
const hint = computed(() => {
  const next = target.value;
  if (!next) return "A command, like npm run dev, or an address on Fleet's machine, like localhost:5173.";
  return next.kind === "address"
    ? `Shows ${next.url} through Fleet.`
    : "Runs it in this session's folder and shows the page it serves.";
});

watch(
  () => props.open,
  async (open) => {
    if (!open) return;
    value.value = "";
    error.value = null;
    touched = false;
    suggestions.value = [];

    try {
      const known = await appRuns.listSessionApps(props.sessionId);
      const commands = [known.previewCommand, ...known.apps.map((app) => app.command).reverse()];
      suggestions.value = [...new Set(commands.filter((command): command is string => !!command))];
      if (!touched && known.previewCommand) value.value = known.previewCommand;
    } catch {
      // Nothing to offer; the field still works.
    }
  },
  { immediate: true },
);

function onInput(): void {
  touched = true;
  error.value = null;
}

function pick(command: string): void {
  touched = true;
  value.value = command;
  error.value = null;
}

async function submit(): Promise<void> {
  const next = target.value;
  if (!next || busy.value) return;

  busy.value = true;
  error.value = null;
  try {
    const canvasId = next.kind === "command"
      ? await appRuns.startCommand(props.sessionId, next.command)
      : await appRuns.openAddress(props.sessionId, next.url);

    // The canvas events bring the tab too; loading it here doesn't depend on the socket.
    canvases.setServerCanvases(props.sessionId, await fetchServerCanvases(props.sessionId));
    canvases.activate(props.sessionId, serverCanvasTabId(canvasId));
    emit("update:open", false);
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <Dialog
    :open="open"
    @update:open="emit('update:open', $event)"
  >
    <DialogContent
      class="browser-open sm:max-w-md"
      data-testid="browser-open-dialog"
    >
      <DialogHeader>
        <DialogTitle>Open a browser tab</DialogTitle>
        <DialogDescription class="sr-only">
          Run a command in this session's folder, or show an address on Fleet's machine.
        </DialogDescription>
      </DialogHeader>

      <form
        class="browser-open__form"
        @submit.prevent="void submit()"
      >
        <label
          for="browser-open-value"
          class="browser-open__label"
        >Command or address</label>
        <Input
          id="browser-open-value"
          v-model="value"
          class="browser-open__input"
          data-testid="browser-open-input"
          placeholder="npm run dev"
          spellcheck="false"
          autocomplete="off"
          :disabled="busy"
          @input="onInput"
        />
        <p class="browser-open__hint">
          {{ hint }}
        </p>

        <div
          v-if="suggestions.length > 0"
          class="browser-open__suggestions"
        >
          <span class="browser-open__suggestions-label">Used here before</span>
          <button
            v-for="command in suggestions"
            :key="command"
            type="button"
            class="browser-open__suggestion"
            :class="{ 'browser-open__suggestion--on': command === value.trim() }"
            :title="command"
            @click="pick(command)"
          >
            {{ command }}
          </button>
        </div>

        <p
          v-if="error"
          class="browser-open__error"
          role="alert"
        >
          {{ error }}
        </p>

        <DialogFooter>
          <Button
            type="submit"
            data-testid="browser-open-submit"
            :disabled="!target || busy"
          >
            {{ target?.kind === "address" ? "Open" : "Run" }}
          </Button>
        </DialogFooter>
      </form>
    </DialogContent>
  </Dialog>
</template>

<style scoped>
.browser-open__form {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.browser-open__label {
  font-size: 13px;
  font-weight: 500;
  color: var(--text);
}

.browser-open__input {
  font-family: var(--font-mono-stack);
  font-size: 12.5px;
}

.browser-open__hint {
  margin: 0;
  font-size: 12px;
  color: var(--muted);
}

.browser-open__suggestions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin-top: 4px;
}

.browser-open__suggestions-label {
  font-size: 11.5px;
  color: var(--muted);
}

.browser-open__suggestion {
  max-width: 100%;
  overflow: hidden;
  padding: 2px 8px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  text-overflow: ellipsis;
  white-space: nowrap;
  transition: background var(--transition), border-color var(--transition);
}

.browser-open__suggestion:hover,
.browser-open__suggestion--on {
  border-color: var(--accent);
  background: var(--accent-dim);
}

.browser-open__error {
  margin: 4px 0 0;
  padding: 6px 10px;
  border: 1px solid color-mix(in srgb, var(--error) 30%, transparent);
  border-radius: var(--radius-btn);
  background: color-mix(in srgb, var(--error) 8%, transparent);
  color: var(--error);
  font-size: 12.5px;
}
</style>
