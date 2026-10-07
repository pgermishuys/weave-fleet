<script setup lang="ts">
import { computed } from "vue";
import { Check, ChevronDown } from "lucide-vue-next";
import { DropdownMenuItem } from "reka-ui";
import type { ProjectResponse } from "@/api/client";
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";

/** A project chip: picks a user project, or Scratch (null). Used by the workflow Run box. */
const props = defineProps<{
  projects: readonly ProjectResponse[];
  disabled?: boolean;
}>();

const emit = defineEmits<{
  closeAutoFocus: [event: Event];
}>();

/** A user project's id; null is Scratch. */
const projectId = defineModel<string | null>({ required: true });

const userProjects = computed(() => props.projects.filter((project) => project.type !== "scratch"));

const selectedName = computed(() =>
  userProjects.value.find((project) => project.id === projectId.value)?.name ?? "Scratch");
</script>

<template>
  <DropdownMenu :modal="false">
    <DropdownMenuTrigger as-child>
      <button
        type="button"
        class="ns-chip"
        data-testid="workflow-project"
        aria-label="Project"
        title="Where the run's sessions go"
        :disabled="disabled"
      >
        <span class="ns-chip__hint">Project:</span>
        <span class="ns-chip__label">{{ selectedName }}</span>
        <ChevronDown
          class="ns-chip__chevron"
          aria-hidden="true"
        />
      </button>
    </DropdownMenuTrigger>

    <DropdownMenuContent
      class="ns-pop ns-project"
      side="top"
      align="start"
      :side-offset="6"
      :collision-padding="8"
      @close-auto-focus="emit('closeAutoFocus', $event)"
    >
      <div class="ns-pop__label">
        Project
      </div>
      <DropdownMenuItem
        class="ns-option ns-project__option"
        @select="projectId = null"
      >
        <span class="ns-option__text">
          <span class="ns-option__title">Scratch</span>
        </span>
        <Check
          v-if="projectId === null"
          class="ns-option__check"
          aria-hidden="true"
        />
      </DropdownMenuItem>
      <DropdownMenuItem
        v-for="project in userProjects"
        :key="project.id"
        class="ns-option ns-project__option"
        @select="projectId = project.id"
      >
        <span class="ns-option__text">
          <span class="ns-option__title">{{ project.name }}</span>
        </span>
        <Check
          v-if="project.id === projectId"
          class="ns-option__check"
          aria-hidden="true"
        />
      </DropdownMenuItem>
    </DropdownMenuContent>
  </DropdownMenu>
</template>

<style>
.ns-project {
  width: 220px;
}

.ns-project__option {
  grid-template-columns: minmax(0, 1fr) 14px;
}
</style>
