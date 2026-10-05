<script setup lang="ts">
import { shallowRef, watch } from "vue";
import { Archive, FileDiff, FolderOpen, GitFork, Laptop, MessageCircleQuestionMark, Pencil, Square, SquareTerminal } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import { Button } from "@/components/ui/button";

/** The session's ⋯ menu: what the desktop's side panels and header actions do, as sheets and rows. */
export type MenuAction = "changes" | "files" | "side" | "fork" | "rename" | "computer" | "terminal" | "archive" | "stop";

const props = defineProps<{
  open: boolean;
  title: string;
  machineName: string;
  changedFiles: number;
  working: boolean;
  supportsSide: boolean;
  canFork: boolean;
}>();
const emit = defineEmits<{ (event: "pick", action: MenuAction): void; (event: "rename", title: string): void; (event: "close"): void }>();

const renaming = shallowRef(false);
const newTitle = shallowRef("");
watch(() => props.open, (open) => {
  renaming.value = false;
  if (open) newTitle.value = props.title;
});

function pick(action: MenuAction): void {
  if (action === "rename") {
    renaming.value = true;
    return;
  }
  emit("pick", action);
}
</script>

<template>
  <BottomSheet
    :open="open"
    label="Session menu"
    @close="emit('close')"
  >
    <form
      v-if="renaming"
      class="msm__rename"
      @submit.prevent="emit('rename', newTitle.trim())"
    >
      <label
        class="msm__label"
        for="phone-rename"
      >Rename</label>
      <input
        id="phone-rename"
        v-model="newTitle"
        class="msm__input"
        maxlength="200"
        data-testid="phone-rename-input"
      >
      <div class="msm__actions">
        <Button
          type="button"
          variant="ghost"
          @click="renaming = false"
        >
          Cancel
        </Button>
        <Button
          type="submit"
          :disabled="!newTitle.trim()"
        >
          Save
        </Button>
      </div>
    </form>
    <template v-else>
      <button
        v-if="working"
        type="button"
        class="msm__row"
        @click="pick('stop')"
      >
        <Square
          :size="16"
          aria-hidden="true"
        />Stop the agent
      </button>
      <button
        type="button"
        class="msm__row"
        data-testid="menu-changes"
        @click="pick('changes')"
      >
        <FileDiff
          :size="16"
          aria-hidden="true"
        />Changes <span class="msm__hint">{{ changedFiles }} file{{ changedFiles === 1 ? "" : "s" }}</span>
      </button>
      <button
        type="button"
        class="msm__row"
        data-testid="menu-files"
        @click="pick('files')"
      >
        <FolderOpen
          :size="16"
          aria-hidden="true"
        />Files
      </button>
      <button
        v-if="supportsSide"
        type="button"
        class="msm__row"
        data-testid="menu-side"
        @click="pick('side')"
      >
        <MessageCircleQuestionMark
          :size="16"
          aria-hidden="true"
        />Side question <span class="msm__hint">/btw</span>
      </button>
      <button
        type="button"
        class="msm__row"
        data-testid="menu-terminal"
        @click="pick('terminal')"
      >
        <SquareTerminal
          :size="16"
          aria-hidden="true"
        />Terminal
      </button>
      <button
        v-if="canFork"
        type="button"
        class="msm__row"
        @click="pick('fork')"
      >
        <GitFork
          :size="16"
          aria-hidden="true"
        />Fork from here
      </button>
      <button
        type="button"
        class="msm__row"
        data-testid="menu-rename"
        @click="pick('rename')"
      >
        <Pencil
          :size="16"
          aria-hidden="true"
        />Rename
      </button>
      <button
        type="button"
        class="msm__row"
        @click="pick('computer')"
      >
        <Laptop
          :size="16"
          aria-hidden="true"
        />Open on my computer
      </button>
      <button
        type="button"
        class="msm__row msm__row--danger"
        data-testid="menu-archive"
        @click="pick('archive')"
      >
        <Archive
          :size="16"
          aria-hidden="true"
        />Archive
      </button>
    </template>
  </BottomSheet>
</template>

<style scoped>
.msm__row {
  display: flex;
  align-items: center;
  gap: 12px;
  width: 100%;
  min-height: 48px;
  padding: 4px;
  border: 0;
  border-bottom: 1px solid var(--border);
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 15px;
  text-align: left;
}

.msm__row--danger {
  color: var(--error);
}

.msm__hint {
  margin-left: auto;
  font-size: 12px;
  color: var(--muted);
}

.msm__rename {
  display: grid;
  gap: 8px;
}

.msm__label {
  font-size: 13px;
  color: var(--muted);
}

.msm__input {
  min-height: 44px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--main-bg);
  color: var(--text);
  font: inherit;
  font-size: 16px;
}

.msm__actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
