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
      <label class="ph-field">
        <Search aria-hidden="true" />
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
      class="ph-foot pfs__looking"
    >
      Looking…
    </p>
    <div
      v-if="files.length"
      class="ph-card"
    >
      <button
        v-for="file in files.slice(0, 50)"
        :key="file"
        type="button"
        class="ph-set"
        @click="emit('pick', file); emit('close')"
      >
        <FileText
          class="ph-set__ic"
          aria-hidden="true"
        />
        <span class="ph-set__main">
          <span class="ph-set__t"><span>{{ file.split("/").pop() }}</span></span>
          <span class="ph-set__s ph-mono pfs__folder">{{ file.split("/").slice(0, -1).join("/") || "." }}</span>
        </span>
      </button>
    </div>
  </BottomSheet>
</template>

<style scoped>
.pfs__search {
  margin-bottom: 12px;
}

.pfs__looking {
  margin-top: 0;
}

.pfs__folder {
  overflow: hidden;
  font-size: 12px;
  white-space: nowrap;
  text-overflow: ellipsis;
}
</style>
