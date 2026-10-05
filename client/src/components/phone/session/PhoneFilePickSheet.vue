<script setup lang="ts">
import { shallowRef, watch } from "vue";
import { FileText, Search } from "lucide-vue-next";
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
    title="Add a file"
    :detents="['large']"
    @close="emit('close')"
  >
    <div class="ph-sheet__pad pfs__search">
      <label class="ph-search">
        <Search
          :size="18"
          aria-hidden="true"
        />
        <input
          v-model="query"
          class="phone-composer-input"
          type="search"
          placeholder="Find a file"
          aria-label="Find a file"
          autocapitalize="off"
          autocomplete="off"
          spellcheck="false"
        >
      </label>
    </div>
    <p
      v-if="isLoading && files.length === 0"
      class="ph-group-f"
    >
      Looking…
    </p>
    <div
      v-if="files.length"
      class="ph-group"
    >
      <button
        v-for="file in files.slice(0, 50)"
        :key="file"
        type="button"
        class="ph-row"
        style="--ph-sep-left: 52px"
        @click="emit('pick', file); emit('close')"
      >
        <FileText
          class="pfs__icon"
          :size="22"
          aria-hidden="true"
        />
        <span class="ph-row__main">
          <span class="ph-row__title pfs__name">{{ file.split("/").pop() }}</span>
          <span class="ph-row__sub">{{ file.split("/").slice(0, -1).join("/") || "." }}</span>
        </span>
      </button>
    </div>
  </BottomSheet>
</template>

<style scoped>
.pfs__search {
  margin-bottom: 14px;
}

.pfs__icon {
  flex: none;
  color: var(--muted);
}

.pfs__name {
  font-family: var(--ph-mono);
  font-size: 0.85rem;
}
</style>
