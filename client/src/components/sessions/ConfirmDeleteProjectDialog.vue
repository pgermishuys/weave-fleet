<script setup lang="ts">
import { watch, shallowRef } from "vue";
import { AlertTriangle } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import {
  AlertDialog,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import type { DeleteProjectMode } from "@/composables/use-session-actions";

interface Props {
  projectName: string;
  sessionCount: number;
  isDeleting?: boolean;
}

interface Emits {
  confirm: [mode: DeleteProjectMode];
}

const open = defineModel<boolean>("open", { default: false });

const props = defineProps<Props>();
const emit = defineEmits<Emits>();

const selectedMode = shallowRef<DeleteProjectMode>("move_to_scratch");

watch(open, (isOpen) => {
  if (isOpen) {
    selectedMode.value = "move_to_scratch";
  }
});

function handleOpenChange(value: boolean): void {
  if (props.isDeleting && !value) {
    return;
  }

  open.value = value;
}

function handleCancel(): void {
  handleOpenChange(false);
}

function handleConfirm(): void {
  emit("confirm", selectedMode.value);
}
</script>

<template>
  <AlertDialog
    :open="open"
    @update:open="handleOpenChange"
  >
    <AlertDialogContent>
      <AlertDialogHeader>
        <AlertDialogTitle class="flex items-center gap-2">
          <AlertTriangle class="h-4 w-4 text-destructive" />
          Delete {{ projectName }}?
        </AlertDialogTitle>
        <AlertDialogDescription>
          Choose what should happen to the
          <span class="font-medium text-foreground">{{ sessionCount }}</span>
          session{{ sessionCount === 1 ? "" : "s" }} in this project.
        </AlertDialogDescription>
      </AlertDialogHeader>

      <div class="grid gap-3">
        <label
          class="flex cursor-pointer items-start gap-3 rounded-lg border border-border bg-muted/20 p-4 text-sm transition-colors hover:bg-muted/40"
          :class="selectedMode === 'move_to_scratch' ? 'border-primary bg-primary/5' : undefined"
        >
          <input
            v-model="selectedMode"
            type="radio"
            value="move_to_scratch"
            class="mt-0.5"
            :disabled="isDeleting"
          >
          <span class="space-y-1">
            <span class="block font-medium text-foreground">Delete project only</span>
            <span class="block text-muted-foreground">Move all sessions to Ungrouped and keep them available.</span>
          </span>
        </label>

        <label
          class="flex cursor-pointer items-start gap-3 rounded-lg border border-destructive/40 bg-destructive/5 p-4 text-sm transition-colors hover:bg-destructive/10"
          :class="selectedMode === 'delete_sessions' ? 'border-destructive bg-destructive/10' : undefined"
        >
          <input
            v-model="selectedMode"
            type="radio"
            value="delete_sessions"
            class="mt-0.5"
            :disabled="isDeleting"
          >
          <span class="space-y-1">
            <span class="block font-medium text-foreground">Delete project and all sessions</span>
            <span class="block text-muted-foreground">This permanently removes the project and every session inside it.</span>
          </span>
        </label>
      </div>

      <AlertDialogFooter>
        <Button
          type="button"
          variant="outline"
          :disabled="isDeleting"
          @click="handleCancel"
        >
          Cancel
        </Button>
        <Button
          type="button"
          variant="destructive"
          :disabled="isDeleting"
          @click="handleConfirm"
        >
          {{ isDeleting ? "Deleting…" : "Delete Project" }}
        </Button>
      </AlertDialogFooter>
    </AlertDialogContent>
  </AlertDialog>
</template>
