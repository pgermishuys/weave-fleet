<script setup lang="ts">
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
import { useCreateProject } from "@/composables/use-session-actions";
import type { ProjectResponse } from "@/lib/api-types";

const open = defineModel<boolean>("open", { default: false });

const emit = defineEmits<{
  created: [project: ProjectResponse];
}>();

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

  try {
    const project = await createProject({
      name: trimmedName.value,
      description: description.value.trim() || undefined,
    });

    open.value = false;
    emit("created", project);
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : "Failed to create project";
  }
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
    await nextTick();
    document.getElementById("new-project-name")?.focus();
    return;
  }

  resetForm();
});
</script>

<template>
  <Dialog :open="open" @update:open="handleOpenChange">
    <DialogContent class="sm:max-w-lg">
      <DialogHeader>
        <DialogTitle>New Project</DialogTitle>
        <DialogDescription>
          Create a project to organize related sessions.
        </DialogDescription>
      </DialogHeader>

      <form class="space-y-5" @submit.prevent="handleSubmit">
        <div class="space-y-2">
          <label for="new-project-name" class="text-sm font-medium text-foreground">Name</label>
          <Input
            id="new-project-name"
            v-model="name"
            autofocus
            placeholder="Project name"
            :disabled="isCreating"
          />
        </div>

        <div class="space-y-2">
          <label for="new-project-description" class="text-sm font-medium text-foreground">Description</label>
          <Textarea
            id="new-project-description"
            v-model="description"
            placeholder="Optional description"
            :disabled="isCreating"
          />
        </div>

        <div
          v-if="errorMessage"
          class="flex items-start gap-3 rounded-md border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
          role="alert"
        >
          <AlertCircle class="mt-0.5 h-4 w-4 shrink-0" />
          <p>{{ errorMessage }}</p>
        </div>

        <DialogFooter>
          <Button type="button" variant="outline" :disabled="isCreating" @click="open = false">
            Cancel
          </Button>

          <Button type="submit" :disabled="!canSubmit">
            <LoaderCircle v-if="isCreating" class="h-4 w-4 animate-spin" />
            {{ isCreating ? "Creating…" : "Create Project" }}
          </Button>
        </DialogFooter>
      </form>
    </DialogContent>
  </Dialog>
</template>
