<script setup lang="ts">
import { nextTick, shallowRef, useTemplateRef, watch } from "vue";
import { Archive, Diff, File, GitFork, Laptop, MessageCircle, Pencil, PanelsTopLeft, Square, Terminal } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import ModDraftMark from "@/components/mods/ModDraftMark.vue";

/** The session's ⋯ menu, a floating sheet drawn as the desktop's dropdown menu: what its side panels and header actions do. */
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
  /** Whether the session's machine has a page to open it on (a node has none). */
  canOpenOnComputer: boolean;
  /** Panes mods opened in this session: each is a menu item that opens its sheet. */
  panes?: readonly { id: string; title: string; draft: boolean }[];
}>();
const emit = defineEmits<{ (event: "pick", action: MenuAction): void; (event: "rename", title: string): void; (event: "pane", id: string): void; (event: "close"): void }>();

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
      class="ph-sheet__pad msm__rename"
      @submit.prevent="emit('rename', newTitle.trim())"
    >
      <div class="ph-label msm__label">
        Rename
      </div>
      <input
        id="phone-rename"
        ref="input"
        v-model="newTitle"
        class="phone-composer-input ph-input"
        maxlength="200"
        aria-label="New title"
        enterkeyhint="done"
        data-testid="phone-rename-input"
      >
      <div class="ph-btns">
        <button
          type="button"
          class="ph-btn ph-btn--outline"
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
    </form>
    <div
      v-else
      class="ph-menu"
      role="menu"
    >
      <button
        type="button"
        class="ph-mi"
        role="menuitem"
        data-testid="menu-changes"
        @click="pick('changes')"
      >
        <Diff aria-hidden="true" />
        <span>Changes</span>
        <span class="ph-mi__hint">{{ changedFiles }} file{{ changedFiles === 1 ? "" : "s" }}</span>
      </button>
      <button
        type="button"
        class="ph-mi"
        role="menuitem"
        data-testid="menu-files"
        @click="pick('files')"
      >
        <File aria-hidden="true" />
        <span>Files</span>
      </button>
      <button
        v-if="supportsSide"
        type="button"
        class="ph-mi"
        role="menuitem"
        data-testid="menu-side"
        @click="pick('side')"
      >
        <MessageCircle aria-hidden="true" />
        <span>Side question</span>
        <span class="ph-mi__hint">/btw</span>
      </button>
      <button
        type="button"
        class="ph-mi"
        role="menuitem"
        data-testid="menu-terminal"
        @click="pick('terminal')"
      >
        <Terminal aria-hidden="true" />
        <span>Run a command</span>
        <span class="ph-mi__hint">!</span>
      </button>
      <button
        v-for="pane in panes ?? []"
        :key="pane.id"
        type="button"
        class="ph-mi"
        role="menuitem"
        data-testid="menu-pane"
        @click="emit('pane', pane.id)"
      >
        <PanelsTopLeft aria-hidden="true" />
        <span>{{ pane.title }}</span>
        <ModDraftMark v-if="pane.draft" />
      </button>
      <hr>
      <button
        v-if="canFork"
        type="button"
        class="ph-mi"
        role="menuitem"
        @click="pick('fork')"
      >
        <GitFork aria-hidden="true" />
        <span>Fork from here</span>
      </button>
      <button
        type="button"
        class="ph-mi"
        role="menuitem"
        data-testid="menu-rename"
        @click="pick('rename')"
      >
        <Pencil aria-hidden="true" />
        <span>Rename</span>
      </button>
      <button
        v-if="canOpenOnComputer"
        type="button"
        class="ph-mi"
        role="menuitem"
        data-testid="menu-computer"
        @click="pick('computer')"
      >
        <Laptop aria-hidden="true" />
        <span>Open on my computer</span>
      </button>
      <hr>
      <button
        v-if="working"
        type="button"
        class="ph-mi"
        role="menuitem"
        data-testid="menu-stop"
        @click="pick('stop')"
      >
        <Square aria-hidden="true" />
        <span>Stop the agent</span>
      </button>
      <button
        type="button"
        class="ph-mi"
        role="menuitem"
        data-testid="menu-archive"
        @click="pick('archive')"
      >
        <Archive aria-hidden="true" />
        <span>Archive</span>
      </button>
    </div>
  </BottomSheet>
</template>

<style scoped>
.msm__rename {
  padding-bottom: 6px;
}

.msm__label {
  margin: 4px 2px 8px;
}
</style>
