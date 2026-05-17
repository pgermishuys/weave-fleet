<script setup lang="ts">
import { computed } from "vue";
import { ExternalLink, Loader2, Code2, AppWindow, Terminal, SquareTerminal, FolderOpen } from "lucide-vue-next";
import {
  ContextMenuSub,
  ContextMenuSubTrigger,
  ContextMenuSubContent,
  ContextMenuItem,
  ContextMenuSeparator,
} from "@/components/ui/context-menu";
import { useAvailableTools, getToolsByCategory, type AvailableTool } from "@/composables/use-available-tools";
import { useOpenDirectory } from "@/composables/use-open-directory";

interface Props {
  directory: string;
}

const props = defineProps<Props>();

const { tools, isLoading, error } = useAvailableTools();
const { openDirectory } = useOpenDirectory();

const iconMap: Record<string, typeof Code2> = {
  "code-2": Code2,
  "app-window": AppWindow,
  "terminal": Terminal,
  "square-terminal": SquareTerminal,
  "folder-open": FolderOpen,
};

interface ToolGroup {
  category: AvailableTool["category"];
  items: AvailableTool[];
}

const groups = computed<ToolGroup[]>(() => {
  const order: AvailableTool["category"][] = ["editor", "terminal", "explorer"];
  const result: ToolGroup[] = [];
  for (const category of order) {
    const items = getToolsByCategory(tools.value, category);
    if (items.length > 0) {
      result.push({ category, items });
    }
  }
  return result;
});

function getIcon(iconName: string) {
  return iconMap[iconName] ?? ExternalLink;
}

function handleOpen(toolId: string): void {
  void openDirectory(props.directory, toolId);
}
</script>

<template>
  <ContextMenuSub>
    <ContextMenuSubTrigger class="gap-2 text-xs">
      <ExternalLink class="size-3.5" />
      Open in...
    </ContextMenuSubTrigger>
    <ContextMenuSubContent>
      <ContextMenuItem
        v-if="isLoading && tools.length === 0"
        disabled
        class="gap-2 text-xs"
      >
        <Loader2 class="size-3.5 animate-spin" />
        Detecting tools…
      </ContextMenuItem>
      <ContextMenuItem
        v-if="!isLoading && tools.length === 0"
        disabled
        class="gap-2 text-xs text-muted-foreground"
      >
        {{ error ? `Error: ${error}` : 'No tools detected' }}
      </ContextMenuItem>
      <template
        v-for="(group, gi) in groups"
        :key="group.category"
      >
        <ContextMenuSeparator v-if="gi > 0" />
        <ContextMenuItem
          v-for="tool in group.items"
          :key="tool.id"
          class="gap-2 text-xs"
          @click="handleOpen(tool.id)"
        >
          <component
            :is="getIcon(tool.iconName)"
            class="size-3.5"
          />
          <span class="flex-1">{{ tool.label }}</span>
        </ContextMenuItem>
      </template>
    </ContextMenuSubContent>
  </ContextMenuSub>
</template>
