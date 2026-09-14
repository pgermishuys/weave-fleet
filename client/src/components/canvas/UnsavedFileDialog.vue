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
import { Button } from "@/components/ui/button";

defineProps<{
  path: string;
  saving?: boolean;
}>();

const emit = defineEmits<{
  save: [];
  discard: [];
}>();

const open = defineModel<boolean>("open", { default: false });
</script>

<template>
  <AlertDialog
    :open="open"
    @update:open="open = $event"
  >
    <AlertDialogContent>
      <AlertDialogHeader>
        <AlertDialogTitle>Save changes before closing?</AlertDialogTitle>
        <AlertDialogDescription>
          <span class="font-mono text-foreground">{{ path }}</span>
          has changes that aren't saved. If you close it without saving, they're lost.
        </AlertDialogDescription>
      </AlertDialogHeader>
      <AlertDialogFooter>
        <AlertDialogCancel
          :disabled="saving"
          data-testid="unsaved-cancel"
        >
          Cancel
        </AlertDialogCancel>
        <Button
          variant="outline"
          :disabled="saving"
          data-testid="unsaved-discard"
          @click="emit('discard')"
        >
          Close without saving
        </Button>
        <AlertDialogAction
          :disabled="saving"
          data-testid="unsaved-save"
          @click.prevent="emit('save')"
        >
          {{ saving ? "Saving…" : "Save and close" }}
        </AlertDialogAction>
      </AlertDialogFooter>
    </AlertDialogContent>
  </AlertDialog>
</template>
