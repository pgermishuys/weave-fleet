<script setup lang="ts">
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
import { ErrorNote } from "@/components/ui/error-note";

defineProps<{
  sessionTitle: string;
  isClearing?: boolean;
  error?: string | null;
}>();
const emit = defineEmits<{ confirm: [] }>();
const open = defineModel<boolean>("open", { default: false });
</script>

<template>
  <AlertDialog
    :open="open"
    @update:open="open = $event"
  >
    <AlertDialogContent>
      <AlertDialogHeader>
        <AlertDialogTitle>Clear attachments?</AlertDialogTitle>
        <AlertDialogDescription>
          This deletes the files attached to
          <span class="font-medium text-foreground">{{ sessionTitle }}</span>.
          The agent can't read them again. The conversation stays.
        </AlertDialogDescription>
        <ErrorNote
          v-if="error"
          :message="error"
          data-testid="clear-attachments-error"
        />
      </AlertDialogHeader>
      <AlertDialogFooter>
        <AlertDialogCancel :disabled="isClearing">
          Cancel
        </AlertDialogCancel>
        <AlertDialogAction
          :disabled="isClearing"
          data-testid="clear-attachments-confirm"
          class="bg-destructive text-white hover:bg-destructive/90"
          @click.prevent="emit('confirm')"
        >
          {{ isClearing ? "Clearing…" : "Clear" }}
        </AlertDialogAction>
      </AlertDialogFooter>
    </AlertDialogContent>
  </AlertDialog>
</template>
