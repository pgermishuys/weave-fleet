<script setup lang="ts">
import { Archive, GitFork, Loader2, OctagonX, Pencil, RotateCcw, Square, Trash2 } from "lucide-vue-next";

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
    <button
      v-if="props.canAbort"
      type="button"
      data-testid="abort-button"
      class="session-action-toolbar__btn session-action-toolbar__btn--danger"
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
    </button>

    <button
      v-if="props.canResume"
      type="button"
      data-testid="session-resume-button"
      class="session-action-toolbar__btn"
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
    </button>

    <button
      v-if="props.canStop"
      type="button"
      data-testid="session-stop-button"
      class="session-action-toolbar__btn session-action-toolbar__btn--danger"
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
    </button>

    <span class="session-action-toolbar__divider" />

    <button
      v-if="props.canFork"
      type="button"
      data-testid="session-archived-fork-button"
      class="session-action-toolbar__btn"
      :disabled="props.isPending || !props.hasSession"
      title="Fork"
      @click="emit('fork')"
    >
      <GitFork aria-hidden="true" />
    </button>

    <button
      v-if="props.canDelete"
      type="button"
      class="session-action-toolbar__btn"
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
    </button>

    <button
      v-if="props.canDelete"
      type="button"
      data-testid="session-delete-button"
      class="session-action-toolbar__btn session-action-toolbar__btn--danger"
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
    </button>

    <button
      v-if="props.canArchive"
      type="button"
      data-testid="session-archive-banner-button"
      class="session-action-toolbar__btn"
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
    </button>

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

.session-action-toolbar__btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  padding: 0;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: transparent;
  color: var(--text);
  cursor: pointer;
}

.session-action-toolbar__btn :deep(svg) {
  width: 12px;
  height: 12px;
}

.session-action-toolbar__btn:hover:not(:disabled) {
  background: rgba(255, 255, 255, 0.1);
}

.session-action-toolbar__btn:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.session-action-toolbar__btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.session-action-toolbar__btn--danger {
  border-color: rgba(239, 68, 68, 0.35);
  color: #fca5a5;
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
