<script setup lang="ts">
import { shallowRef, watch } from "vue";
import { FileText } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import { useFindFiles } from "@/composables/use-find-files";

/** Find a file in the session's folder and add it as an `@` reference, as the desktop's `@` list does. */
const props = defineProps<{ open: boolean; sessionId: string }>();
const emit = defineEmits<{ (event: "pick", path: string): void; (event: "close"): void }>();

const query = shallowRef("");
const { files, isLoading } = useFindFiles(() => props.sessionId, () => (props.open ? query.value : null));
watch(() => props.open, (open) => {
  if (open) query.value = "";
});
</script>

<template>
  <BottomSheet
    :open="open"
    label="Add a file"
    full
    @close="emit('close')"
  >
    <input
      v-model="query"
      class="pfs__input"
      type="search"
      placeholder="Find a file"
      aria-label="Find a file"
      autocapitalize="off"
      autocomplete="off"
      spellcheck="false"
    >
    <p
      v-if="isLoading && files.length === 0"
      class="pfs__empty"
    >
      Looking…
    </p>
    <button
      v-for="file in files.slice(0, 50)"
      :key="file"
      type="button"
      class="pfs__row"
      @click="emit('pick', file); emit('close')"
    >
      <FileText
        :size="15"
        aria-hidden="true"
      /><span class="pfs__path">{{ file }}</span>
    </button>
  </BottomSheet>
</template>

<style scoped>
.pfs__input {
  width: 100%;
  min-height: 44px;
  margin-bottom: 8px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--main-bg);
  color: var(--text);
  font: inherit;
  font-size: 16px;
}

.pfs__empty {
  font-size: 13px;
  color: var(--muted);
}

.pfs__row {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  min-height: 44px;
  border: 0;
  border-bottom: 1px solid var(--border);
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 13px;
  text-align: left;
}

.pfs__path {
  overflow: hidden;
  font-family: var(--font-mono-stack);
  font-size: 12px;
  white-space: nowrap;
  text-overflow: ellipsis;
  direction: rtl;
  text-align: left;
}
</style>
