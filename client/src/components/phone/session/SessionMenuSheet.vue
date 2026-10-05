<script setup lang="ts">
import { nextTick, shallowRef, useTemplateRef, watch } from "vue";
import { Archive, FileDiff, FileText, GitFork, Laptop, MessageCircle, Pencil, Square, SquareTerminal } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";

/** The session's ⋯ menu, a floating sheet: what the desktop's side panels and header actions do, as grouped rows. */
export type MenuAction = "changes" | "files" | "side" | "fork" | "rename" | "computer" | "terminal" | "archive" | "stop";

const props = defineProps<{
  open: boolean;
  title: string;
  machineName: string;
  changedFiles: number;
  working: boolean;
  supportsSide: boolean;
  supportsShell?: boolean;
  canFork: boolean;
}>();
const emit = defineEmits<{ (event: "pick", action: MenuAction): void; (event: "rename", title: string): void; (event: "close"): void }>();

const renaming = shallowRef(false);
const newTitle = shallowRef("");
const input = useTemplateRef<HTMLInputElement>("input");
watch(() => props.open, (open) => {
  renaming.value = false;
  if (open) newTitle.value = props.title;
});

async function pick(action: MenuAction): Promise<void> {
  if (action === "rename") {
    renaming.value = true;
    await nextTick();
    input.value?.focus();
    input.value?.select();
    return;
  }
  emit("pick", action);
}
</script>

<template>
  <BottomSheet
    :open="open"
    label="Session menu"
    floating
    @close="emit('close')"
  >
    <form
      v-if="renaming"
      class="msm__rename"
      @submit.prevent="emit('rename', newTitle.trim())"
    >
      <div class="ph-group-h msm__label">
        Rename
      </div>
      <div class="ph-sheet__pad">
        <input
          id="phone-rename"
          ref="input"
          v-model="newTitle"
          class="ph-field msm__input"
          maxlength="200"
          aria-label="New title"
          enterkeyhint="done"
          data-testid="phone-rename-input"
        >
        <div class="ph-btns">
          <button
            type="button"
            class="ph-btn ph-btn--plain"
            @click="renaming = false"
          >
            Cancel
          </button>
          <button
            type="submit"
            class="ph-btn ph-btn--primary"
            :disabled="!newTitle.trim()"
          >
            Save
          </button>
        </div>
      </div>
    </form>
    <template v-else>
      <div class="ph-group">
        <button
          type="button"
          class="ph-row"
          style="--ph-sep-left: 56px"
          data-testid="menu-changes"
          @click="pick('changes')"
        >
          <span class="ph-row__icon ph-row__icon--plain"><FileDiff
            :size="24"
            aria-hidden="true"
          /></span>
          <span class="ph-row__main"><span class="ph-row__title">Changes</span></span>
          <span class="ph-row__value">{{ changedFiles }} file{{ changedFiles === 1 ? "" : "s" }}</span>
        </button>
        <button
          type="button"
          class="ph-row"
          style="--ph-sep-left: 56px"
          data-testid="menu-files"
          @click="pick('files')"
        >
          <span class="ph-row__icon ph-row__icon--plain"><FileText
            :size="24"
            aria-hidden="true"
          /></span>
          <span class="ph-row__main"><span class="ph-row__title">Files</span></span>
        </button>
        <button
          v-if="supportsSide"
          type="button"
          class="ph-row"
          style="--ph-sep-left: 56px"
          data-testid="menu-side"
          @click="pick('side')"
        >
          <span class="ph-row__icon ph-row__icon--plain"><MessageCircle
            :size="24"
            aria-hidden="true"
          /></span>
          <span class="ph-row__main"><span class="ph-row__title">Side question</span></span>
          <span class="ph-row__value">/btw</span>
        </button>
        <button
          type="button"
          class="ph-row"
          style="--ph-sep-left: 56px"
          data-testid="menu-terminal"
          @click="pick('terminal')"
        >
          <span class="ph-row__icon ph-row__icon--plain"><SquareTerminal
            :size="24"
            aria-hidden="true"
          /></span>
          <span class="ph-row__main"><span class="ph-row__title">Run a command</span></span>
        </button>
      </div>
      <div class="ph-group">
        <button
          v-if="canFork"
          type="button"
          class="ph-row"
          style="--ph-sep-left: 56px"
          @click="pick('fork')"
        >
          <span class="ph-row__icon ph-row__icon--plain"><GitFork
            :size="24"
            aria-hidden="true"
          /></span>
          <span class="ph-row__main"><span class="ph-row__title">Fork from here</span></span>
        </button>
        <button
          type="button"
          class="ph-row"
          style="--ph-sep-left: 56px"
          data-testid="menu-rename"
          @click="pick('rename')"
        >
          <span class="ph-row__icon ph-row__icon--plain"><Pencil
            :size="24"
            aria-hidden="true"
          /></span>
          <span class="ph-row__main"><span class="ph-row__title">Rename</span></span>
        </button>
        <button
          type="button"
          class="ph-row"
          style="--ph-sep-left: 56px"
          data-testid="menu-computer"
          @click="pick('computer')"
        >
          <span class="ph-row__icon ph-row__icon--plain"><Laptop
            :size="24"
            aria-hidden="true"
          /></span>
          <span class="ph-row__main"><span class="ph-row__title">Open on my computer</span></span>
        </button>
      </div>
      <div class="ph-group">
        <button
          v-if="working"
          type="button"
          class="ph-row"
          style="--ph-sep-left: 56px"
          data-testid="menu-stop"
          @click="pick('stop')"
        >
          <span class="ph-row__icon ph-row__icon--plain"><Square
            :size="20"
            aria-hidden="true"
          /></span>
          <span class="ph-row__main"><span class="ph-row__title">Stop the agent</span></span>
        </button>
        <button
          type="button"
          class="ph-row ph-row--danger"
          style="--ph-sep-left: 56px"
          data-testid="menu-archive"
          @click="pick('archive')"
        >
          <span class="ph-row__icon ph-row__icon--plain"><Archive
            :size="24"
            aria-hidden="true"
          /></span>
          <span class="ph-row__main"><span class="ph-row__title">Archive</span></span>
        </button>
      </div>
    </template>
  </BottomSheet>
</template>

<style scoped>
.ph-group:first-child {
  margin-top: 4px;
}

.msm__label {
  margin-top: 4px;
}

.msm__input {
  margin-bottom: 4px;
}
</style>
