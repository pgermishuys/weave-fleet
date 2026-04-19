<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { useForkSession } from "@/composables/use-session-actions";

interface Props {
  sessionId: string;
  sourceTitle: string;
  open?: boolean;
}

const props = withDefaults(defineProps<Props>(), {
  open: false,
});
const emit = defineEmits<{
  "update:open": [value: boolean];
}>();

const router = useRouter();
const title = shallowRef("");
const { forkSession, clearError, isForking, error } = useForkSession();

const resolvedSourceTitle = computed(() => props.sourceTitle.trim() || "Untitled session");

function resetForm(): void {
  title.value = resolvedSourceTitle.value;
}

function handleOpenChange(value: boolean): void {
  emit("update:open", value);
}

async function handleSubmit(): Promise<void> {
  try {
    const response = await forkSession(props.sessionId, {
      title: title.value.trim() || undefined,
    });

    emit("update:open", false);

    await router.navigate({
      to: "/sessions/$id",
      params: { id: response.session.id },
      search: {
        instanceId: response.instanceId,
        parentSessionId: undefined,
      },
    });
  } catch {
    // Error exposed via composable state.
  }
}

watch(
  [() => props.open, resolvedSourceTitle],
  ([isOpen]) => {
    clearError();

    if (isOpen) {
      resetForm();
      return;
    }

    resetForm();
  },
  { immediate: true },
);
</script>

<template>
  <Dialog :open="props.open" @update:open="handleOpenChange">
    <DialogContent class="sm:max-w-lg" data-testid="fork-session-dialog">
      <DialogHeader>
        <DialogTitle data-testid="fork-session-dialog-title">
          Fork Session
        </DialogTitle>
      </DialogHeader>

      <form class="space-y-4" @submit.prevent="void handleSubmit()">
        <div class="space-y-2">
          <p class="text-sm font-medium text-foreground">Source session</p>
          <p data-testid="fork-session-source-title" class="text-sm text-muted-foreground">
            {{ resolvedSourceTitle }}
          </p>
        </div>

        <div class="space-y-2">
          <label for="fork-session-title" class="text-sm font-medium text-foreground">
            New session title
          </label>
          <Input
            id="fork-session-title"
            v-model="title"
            data-testid="fork-session-title-input"
            placeholder="Enter a title"
            :disabled="isForking"
          />
        </div>

        <p
          v-if="error"
          class="rounded-lg border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
          role="alert"
        >
          {{ error }}
        </p>

        <DialogFooter>
          <Button
            data-testid="fork-session-submit"
            type="submit"
            :disabled="isForking"
          >
            {{ isForking ? "Forking…" : "Fork session" }}
          </Button>
        </DialogFooter>
      </form>
    </DialogContent>
  </Dialog>
</template>
