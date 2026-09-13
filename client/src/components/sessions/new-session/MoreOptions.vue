<script setup lang="ts">
import { computed } from "vue";
import { Ellipsis } from "lucide-vue-next";
import type { ProjectResponse } from "@/api/client";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";

const props = defineProps<{
  projects: readonly ProjectResponse[];
  disabled?: boolean;
}>();

const emit = defineEmits<{
  closeAutoFocus: [event: Event];
}>();

const open = defineModel<boolean>("open", { default: false });
/** A user project's id; null lands the session in Scratch. */
const projectId = defineModel<string | null>("projectId", { required: true });
const title = defineModel<string>("title", { required: true });
const tags = defineModel<string>("tags", { required: true });

const userProjects = computed(() => props.projects.filter((project) => project.type !== "scratch"));

const selectedProjectName = computed(() => {
  return userProjects.value.find((project) => project.id === projectId.value)?.name ?? null;
});

function handleProjectChange(event: Event): void {
  const value = (event.target as HTMLSelectElement).value;
  projectId.value = value || null;
}
</script>

<template>
  <Popover v-model:open="open">
    <PopoverTrigger as-child>
      <button
        type="button"
        class="ns-chip"
        data-testid="new-session-more-chip"
        :disabled="disabled"
        :aria-label="selectedProjectName ? `More options (project ${selectedProjectName}): project, title, tags` : 'More options: project, title, tags'"
        title="Project, title, tags"
      >
        <span
          v-if="selectedProjectName"
          class="ns-chip__label"
        >{{ selectedProjectName }}</span>
        <Ellipsis
          class="ns-chip__icon"
          aria-hidden="true"
        />
      </button>
    </PopoverTrigger>

    <PopoverContent
      class="ns-pop ns-more"
      side="top"
      align="end"
      :side-offset="6"
      :collision-padding="8"
      @close-auto-focus="emit('closeAutoFocus', $event)"
    >
      <div
        v-if="userProjects.length > 0"
        class="ns-field"
      >
        <label
          for="new-session-project"
          class="ns-field__label"
        >Project</label>
        <select
          id="new-session-project"
          class="ns-field__input"
          :value="projectId ?? ''"
          @change="handleProjectChange"
        >
          <option value="">
            Scratch
          </option>
          <option
            v-for="project in userProjects"
            :key="project.id"
            :value="project.id"
          >
            {{ project.name }}
          </option>
        </select>
      </div>
      <div class="ns-field">
        <label
          for="session-title"
          class="ns-field__label"
        >Title</label>
        <input
          id="session-title"
          v-model="title"
          type="text"
          class="ns-field__input"
          placeholder="Taken from your first message"
          autocomplete="off"
          @keydown.enter.prevent="open = false"
        >
      </div>
      <div class="ns-field">
        <label
          for="new-session-tags"
          class="ns-field__label"
        >Tags</label>
        <input
          id="new-session-tags"
          v-model="tags"
          type="text"
          class="ns-field__input"
          placeholder="review-requested, deploy"
          autocomplete="off"
          @keydown.enter.prevent="open = false"
        >
      </div>
    </PopoverContent>
  </Popover>
</template>

<style>
.ns-more {
  width: 300px;
}
</style>
