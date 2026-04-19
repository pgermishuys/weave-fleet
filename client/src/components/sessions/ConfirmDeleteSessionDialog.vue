<script setup lang="ts">
import { AlertTriangle } from "lucide-vue-next";
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
  isDeleting?: boolean;
}

interface Emits {
  confirm: [];
}

const open = defineModel<boolean>("open", { default: false });

defineProps<Props>();
const emit = defineEmits<Emits>();

function handleOpenChange(value: boolean): void {
  open.value = value;
}

function handleConfirm(): void {
  emit("confirm");
}
</script>

<template>
  <AlertDialog :open="open" @update:open="handleOpenChange">
    <AlertDialogContent>
      <AlertDialogHeader>
        <AlertDialogTitle class="flex items-center gap-2">
          <AlertTriangle class="h-4 w-4 text-destructive" />
          Are you sure?
        </AlertDialogTitle>
        <AlertDialogDescription>
          This will permanently delete
          <span class="font-medium text-foreground">{{ sessionTitle }}</span>
          and its associated session data.
        </AlertDialogDescription>
      </AlertDialogHeader>

      <AlertDialogFooter>
        <AlertDialogCancel
          :disabled="isDeleting"
          data-testid="delete-dialog-cancel"
        >
          Cancel
        </AlertDialogCancel>
        <AlertDialogAction
          :disabled="isDeleting"
          data-testid="delete-dialog-confirm"
          class="bg-destructive text-white hover:bg-destructive/90 focus-visible:ring-destructive/20 dark:focus-visible:ring-destructive/40"
          @click="handleConfirm"
        >
          {{ isDeleting ? "Deleting…" : "Delete" }}
        </AlertDialogAction>
      </AlertDialogFooter>
    </AlertDialogContent>
  </AlertDialog>
</template>
