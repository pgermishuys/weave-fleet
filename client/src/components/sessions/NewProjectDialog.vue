<script setup lang="ts">
import "@/components/sessions/new-session/new-session.css";
import { computed, nextTick, shallowRef, watch } from "vue";
import { AlertCircle, LoaderCircle } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import MachinePicker from "@/components/sessions/new-session/MachinePicker.vue";
import { useCreateProject } from "@/composables/use-session-actions";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { useMachinesStore } from "@/stores/machines";
import { useNavigate } from "@tanstack/vue-router";
import type { ProjectResponse } from "@/api/client";

/**
 * New Project. With more than one machine it asks which machine the project goes on ("Create on"), starting on
 * `machineKey` (a machine heading's New project on …) or else the machine you're on; with one machine it doesn't ask.
 */
const props = defineProps<{
  /** The machine picked when the dialog opens; the machine you're on when left out. */
  machineKey?: string | null;
}>();

const open = defineModel<boolean>("open", { default: false });

const emit = defineEmits<{
  /** The project, and the key of the machine it was created on. */
  created: [project: ProjectResponse, machineKey: string];
}>();

const machines = useMachinesStore();
const { openAddMachine } = useSettingsNav();
const navigate = useNavigate();
/** The machine the project is created on. */
const pickedKey = shallowRef(machines.currentKey);
const pickedEntry = computed(() => machines.entries.find((entry) => entry.key === pickedKey.value) ?? machines.live);

const name = shallowRef("");
const description = shallowRef("");
const errorMessage = shallowRef<string | null>(null);

const {
  createProject,
  isCreating,
} = useCreateProject();

const trimmedName = computed(() => name.value.trim());
const canSubmit = computed(() => !isCreating.value && trimmedName.value.length > 0);

function resetForm(): void {
  name.value = "";
  description.value = "";
  errorMessage.value = null;
}

function handleOpenChange(value: boolean): void {
  open.value = value;
}

async function handleSubmit(): Promise<void> {
  if (!canSubmit.value) {
    return;
  }

  errorMessage.value = null;

  const machine = pickedEntry.value;
  try {
    const project = await createProject({
      name: trimmedName.value,
      description: description.value.trim() || undefined,
    }, machines.targetOfKey(machine.key));

    open.value = false;
    emit("created", project, machine.key);
  } catch (error) {
    // The fetch itself failed (not an answer from the machine): say which machine, not "Failed to fetch".
    errorMessage.value = error instanceof TypeError && machines.hasMachines
      ? `Can't reach ${machine.name}. The project wasn't created.`
      : error instanceof Error ? error.message : "Failed to create project";
  }
}

function pickMachine(machineKey: string): void {
  pickedKey.value = machineKey;
  errorMessage.value = null;
}

function addMachine(): void {
  open.value = false;
  openAddMachine();
  void navigate({ to: "/settings" });
}

watch(name, () => {
  if (errorMessage.value) {
    errorMessage.value = null;
  }
});

watch(description, () => {
  if (errorMessage.value) {
    errorMessage.value = null;
  }
});

watch(open, async (isOpen) => {
  if (isOpen) {
    errorMessage.value = null;
    pickedKey.value = props.machineKey ?? machines.currentKey;
    await nextTick();
    document.getElementById("new-project-name")?.focus();
    return;
  }

  resetForm();
});
</script>

<template>
  <Dialog
    :open="open"
    @update:open="handleOpenChange"
  >
    <DialogContent class="sm:max-w-lg">
      <DialogHeader>
        <DialogTitle>New Project</DialogTitle>
        <DialogDescription>
          Create a project to organize related sessions.
        </DialogDescription>
      </DialogHeader>

      <form
        class="space-y-5"
        @submit.prevent="handleSubmit"
      >
        <div class="space-y-2">
          <label
            for="new-project-name"
            class="text-sm font-medium text-foreground"
          >Name</label>
          <Input
            id="new-project-name"
            v-model="name"
            autofocus
            placeholder="Project name"
            :disabled="isCreating"
          />
        </div>

        <div
          v-if="machines.hasMachines"
          class="space-y-2"
        >
          <span
            id="new-project-machine-label"
            class="block text-sm font-medium text-foreground"
          >Create on</span>
          <div
            class="new-project-machine"
            role="group"
            aria-labelledby="new-project-machine-label"
          >
            <MachinePicker
              :machines="machines.entries"
              :selected="pickedEntry.key"
              :others="machines.others"
              :live-reachable="machines.liveReachable"
              :disabled="isCreating"
              heading="Create on"
              side="bottom"
              @update:selected="pickMachine"
              @add-machine="addMachine"
            />
          </div>
          <p class="text-xs text-muted">
            The project and its sessions live on this machine.
          </p>
        </div>

        <div class="space-y-2">
          <label
            for="new-project-description"
            class="text-sm font-medium text-foreground"
          >Description</label>
          <Textarea
            id="new-project-description"
            v-model="description"
            placeholder="Optional description"
            :disabled="isCreating"
          />
        </div>

        <div
          v-if="errorMessage"
          class="flex items-start gap-3 border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
          role="alert"
        >
          <AlertCircle class="mt-0.5 h-4 w-4 shrink-0" />
          <p>{{ errorMessage }}</p>
        </div>

        <DialogFooter>
          <Button
            type="button"
            variant="outline"
            :disabled="isCreating"
            @click="open = false"
          >
            Cancel
          </Button>

          <Button
            type="submit"
            :disabled="!canSubmit"
          >
            <LoaderCircle
              v-if="isCreating"
              class="h-4 w-4 animate-spin"
            />
            {{ isCreating ? "Creating…" : "Create Project" }}
          </Button>
        </DialogFooter>
      </form>
    </DialogContent>
  </Dialog>
</template>

<style scoped>
/* The new-session chip, framed like the dialog's fields so it reads as one. */
.new-project-machine :deep(.ns-chip) {
  height: 32px;
  padding: 0 10px;
  border-color: var(--border);
}
</style>
