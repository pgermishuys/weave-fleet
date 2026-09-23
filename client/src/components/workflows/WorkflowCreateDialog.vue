<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
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
import { slugOf, type WorkflowFile } from "@/lib/workflow-draft";
import { useWorkflowsStore } from "@/stores/workflows";

/**
 * New workflow, or Duplicate a built-in: a name, and the file Fleet makes in the repository's `.weave/workflows/`,
 * uncommitted. A name that's taken is refused with the server's message.
 */
const props = defineProps<{
  /** The repository picked in the Library; null when none is. */
  repository: string | null;
  repositoryName: string | null;
  /** Duplicate: the built-in to copy, and its name. New otherwise. */
  source?: { id: string; name: string } | null;
}>();

const emit = defineEmits<{
  created: [file: WorkflowFile];
}>();

const open = defineModel<boolean>("open", { default: false });

const store = useWorkflowsStore();
const name = shallowRef("");
const error = shallowRef<string | null>(null);
const isCreating = shallowRef(false);

watch(open, (isOpen) => {
  if (!isOpen) return;
  name.value = props.source ? `${props.source.name}, our way` : "";
  error.value = null;
});

const file = computed(() => `${props.repositoryName ?? "this repo"}/.weave/workflows/${slugOf(name.value)}.yaml`);
const canCreate = computed(() => !isCreating.value && name.value.trim().length > 0 && props.repository !== null);

async function create(): Promise<void> {
  if (!canCreate.value || !props.repository) return;
  isCreating.value = true;
  error.value = null;
  try {
    const created = await store.createFile(props.repository, name.value.trim(), props.source?.id);
    open.value = false;
    emit("created", created);
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "Couldn't create the workflow file.";
  } finally {
    isCreating.value = false;
  }
}
</script>

<template>
  <Dialog
    :open="open"
    @update:open="open = $event"
  >
    <DialogContent
      class="sm:max-w-md"
      data-testid="workflow-create-dialog"
    >
      <DialogHeader>
        <DialogTitle>{{ source ? `Duplicate ${source.name}` : "New workflow" }}</DialogTitle>
        <DialogDescription v-if="source">
          Fleet copies it into this repo's <code>.weave/workflows/</code> folder and opens the copy in the designer. It's
          an ordinary file in your checkout: commit it when you're ready to share it.
        </DialogDescription>
        <DialogDescription v-else>
          Fleet creates the file in this repo's <code>.weave/workflows/</code> folder with one agent step, so it's valid
          from the start, and opens it in the designer.
        </DialogDescription>
      </DialogHeader>

      <form
        class="space-y-4"
        @submit.prevent="create"
      >
        <div class="space-y-2">
          <label
            for="workflow-create-name"
            class="text-sm font-medium text-foreground"
          >Name</label>
          <Input
            id="workflow-create-name"
            v-model="name"
            autofocus
            placeholder="e.g. Tidy up a flaky test"
            :disabled="isCreating"
            data-testid="workflow-create-name"
          />
        </div>
        <div class="space-y-2">
          <span class="text-sm font-medium text-foreground">File</span>
          <p
            class="truncate rounded-md border border-border px-2 py-1.5 font-mono text-xs text-muted-foreground"
            data-testid="workflow-create-file"
          >
            {{ file }}
          </p>
        </div>

        <p
          v-if="!repository"
          class="text-sm text-muted-foreground"
        >
          Pick a repository in the Run box first: the file goes in that repository.
        </p>
        <div
          v-if="error"
          class="flex items-start gap-3 border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
          role="alert"
          data-testid="workflow-create-error"
        >
          <AlertCircle class="mt-0.5 h-4 w-4 shrink-0" />
          <p>{{ error }}</p>
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
            :disabled="!canCreate"
            data-testid="workflow-create-submit"
          >
            <LoaderCircle
              v-if="isCreating"
              class="h-4 w-4 animate-spin"
            />
            {{ source ? "Duplicate and open" : "Create and open" }}
          </Button>
        </DialogFooter>
      </form>
    </DialogContent>
  </Dialog>
</template>
