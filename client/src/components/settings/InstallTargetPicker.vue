<script setup lang="ts">
import { computed, useId } from "vue";
import { useRepositories } from "@/composables/use-repositories";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { targetFromKey, targetKey, type InstallTarget } from "@/lib/install-target";

const props = defineProps<{
  /** What gets written, so the hint can show where: a skill folder, a tool file, or opencode.json. */
  kind: "skills" | "tools" | "config";
  disabled?: boolean;
}>();

const target = defineModel<InstallTarget>({ required: true });

const id = useId();
const { repositories, isLoading } = useRepositories();

const selectedKey = computed({
  get: () => targetKey(target.value),
  set: (key: string) => {
    target.value = targetFromKey(key);
  },
});

const destination = computed(() => {
  const current = target.value;
  if (current.scope === "global") {
    return props.kind === "config" ? "~/.config/opencode/opencode.json" : `~/.config/opencode/${props.kind}/`;
  }
  // A project's MCP servers go in the opencode.json at the repository root.
  return props.kind === "config"
    ? `${current.projectPath}/opencode.json`
    : `${current.projectPath}/.opencode/${props.kind}/`;
});

const audience = computed(() =>
  target.value.scope === "global" ? "Every session" : "Sessions in this repository",
);
</script>

<template>
  <div class="grid gap-1 text-sm text-text">
    <label
      :for="id"
      class="text-xs font-medium uppercase tracking-wide text-muted"
    >Install to</label>
    <Select
      v-model="selectedKey"
      :disabled="disabled"
    >
      <SelectTrigger
        :id="id"
        class="w-full"
      >
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value="global">
          Global
        </SelectItem>
        <SelectGroup v-if="repositories.length > 0">
          <SelectLabel>Repository</SelectLabel>
          <SelectItem
            v-for="repository in repositories"
            :key="repository.path"
            :value="`project:${repository.path}`"
          >
            {{ repository.name }}
          </SelectItem>
        </SelectGroup>
        <SelectLabel v-else-if="isLoading">
          Loading repositories…
        </SelectLabel>
      </SelectContent>
    </Select>
    <p class="text-xs text-muted">
      {{ audience }} ·
      <span class="break-all font-mono">{{ destination }}</span>
    </p>
  </div>
</template>
