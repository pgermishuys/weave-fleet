<script setup lang="ts">
import { Archive, GitFork, Loader2, OctagonX, Pencil, RotateCcw, Square, Trash2 } from "lucide-vue-next";
import { Button } from "@/components/ui/button";

const props = withDefaults(defineProps<{
  canAbort?: boolean;
  canResume?: boolean;
  canStop?: boolean;
  canArchive?: boolean;
  canFork?: boolean;
  canDelete?: boolean;
  isPending?: boolean;
  isAborting?: boolean;
  isResuming?: boolean;
  isTerminating?: boolean;
  isRenaming?: boolean;
  isDeleting?: boolean;
  isArchiving?: boolean;
  hasSession?: boolean;
  hasInstance?: boolean;
  errors?: readonly string[];
}>(), {
  canFork: true,
  canDelete: true,
});

const emit = defineEmits<{
  abort: [];
  resume: [];
  stop: [];
  fork: [];
  rename: [];
  delete: [];
  archive: [];
}>();
</script>

<template>
  <div
    class="session-action-toolbar"
    aria-label="Session actions"
  >
    <Button
      v-if="props.canAbort"
      variant="toolbar-icon-danger"
      size="toolbar"
      data-testid="abort-button"
      :disabled="props.isPending || !props.hasSession || !props.hasInstance"
      title="Abort"
      @click="emit('abort')"
    >
      <Loader2
        v-if="props.isAborting"
        class="session-action-toolbar__spinner"
        aria-hidden="true"
      />
      <OctagonX
        v-else
        aria-hidden="true"
      />
    </Button>

    <Button
      v-if="props.canResume"
      variant="toolbar-icon"
      size="toolbar"
      data-testid="session-resume-button"
      :disabled="props.isPending || !props.hasSession"
      title="Resume"
      @click="emit('resume')"
    >
      <Loader2
        v-if="props.isResuming"
        class="session-action-toolbar__spinner"
        aria-hidden="true"
      />
      <RotateCcw
        v-else
        aria-hidden="true"
      />
    </Button>

    <Button
      v-if="props.canStop"
      variant="toolbar-icon-danger"
      size="toolbar"
      data-testid="session-stop-button"
      :disabled="props.isPending || !props.hasSession || !props.hasInstance"
      title="Stop"
      @click="emit('stop')"
    >
      <Loader2
        v-if="props.isTerminating"
        class="session-action-toolbar__spinner"
        aria-hidden="true"
      />
      <Square
        v-else
        aria-hidden="true"
      />
    </Button>

    <span class="session-action-toolbar__divider" />

    <Button
      v-if="props.canFork"
      variant="toolbar-icon"
      size="toolbar"
      data-testid="session-archived-fork-button"
      :disabled="props.isPending || !props.hasSession"
      title="Fork"
      @click="emit('fork')"
    >
      <GitFork aria-hidden="true" />
    </Button>

    <Button
      v-if="props.canDelete"
      variant="toolbar-icon"
      size="toolbar"
      :disabled="props.isPending || !props.hasSession"
      title="Rename"
      @click="emit('rename')"
    >
      <Loader2
        v-if="props.isRenaming"
        class="session-action-toolbar__spinner"
        aria-hidden="true"
      />
      <Pencil
        v-else
        aria-hidden="true"
      />
    </Button>

    <Button
      v-if="props.canDelete"
      variant="toolbar-icon-danger"
      size="toolbar"
      data-testid="session-delete-button"
      :disabled="props.isPending || !props.hasSession || !props.hasInstance"
      title="Delete"
      @click="emit('delete')"
    >
      <Loader2
        v-if="props.isDeleting"
        class="session-action-toolbar__spinner"
        aria-hidden="true"
      />
      <Trash2
        v-else
        aria-hidden="true"
      />
    </Button>

    <Button
      v-if="props.canArchive"
      variant="toolbar-icon"
      size="toolbar"
      data-testid="session-archive-banner-button"
      :disabled="props.isPending || !props.hasSession"
      title="Archive"
      @click="emit('archive')"
    >
      <Loader2
        v-if="props.isArchiving"
        class="session-action-toolbar__spinner"
        aria-hidden="true"
      />
      <Archive
        v-else
        aria-hidden="true"
      />
    </Button>

    <p
      v-for="message in props.errors ?? []"
      :key="message"
      class="session-action-toolbar__error"
      role="alert"
    >
      {{ message }}
    </p>
  </div>
</template>

<style scoped>
.session-action-toolbar {
  display: flex;
  align-items: center;
  gap: 2px;
  flex-wrap: wrap;
}

.session-action-toolbar__divider {
  width: 1px;
  height: 16px;
  margin-inline: 2px;
  background: var(--border);
}

.session-action-toolbar__spinner {
  animation: session-action-toolbar-spin 0.8s linear infinite;
}

.session-action-toolbar__error {
  width: 100%;
  margin: 2px 0 0;
  font-size: 10px;
  color: var(--error);
}

@keyframes session-action-toolbar-spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}
</style>
