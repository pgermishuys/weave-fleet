<script setup lang="ts">
import { shallowRef } from "vue";
import { CircleCheck } from "lucide-vue-next";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";

interface Props {
  sessionTitle: string;
  isArchiving?: boolean;
  hasWorktree?: boolean;
}

interface Emits {
  confirm: [deleteWorktree: boolean];
}

const open = defineModel<boolean>("open", { default: false });

const props = defineProps<Props>();
const emit = defineEmits<Emits>();

const deleteWorktree = shallowRef(true);

function handleOpenChange(value: boolean): void {
  open.value = value;
}

function handleConfirm(): void {
  emit("confirm", props.hasWorktree ? deleteWorktree.value : false);
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
          <CircleCheck class="h-4 w-4 text-primary" />
          Complete session?
        </AlertDialogTitle>
        <AlertDialogDescription>
          This will mark
          <span class="font-medium text-foreground">{{ sessionTitle }}</span>
          as completed and archive it.
        </AlertDialogDescription>
      </AlertDialogHeader>

      <label
        v-if="hasWorktree"
        class="flex items-center gap-2 text-sm text-muted-foreground cursor-pointer select-none"
      >
        <input
          v-model="deleteWorktree"
          type="checkbox"
          class="rounded border-muted-foreground/50 accent-primary"
        >
        Delete associated worktree
      </label>

      <AlertDialogFooter>
        <AlertDialogCancel
          :disabled="isArchiving"
          data-testid="complete-dialog-cancel"
        >
          Cancel
        </AlertDialogCancel>
        <AlertDialogAction
          :disabled="isArchiving"
          data-testid="complete-dialog-confirm"
          @click="handleConfirm"
        >
          {{ isArchiving ? "Completing…" : "Complete" }}
        </AlertDialogAction>
      </AlertDialogFooter>
    </AlertDialogContent>
  </AlertDialog>
</template>
