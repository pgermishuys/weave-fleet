<script setup lang="ts">
import { computed, watch } from "vue";
import { ExternalLink, FileText, Folder, Loader2 } from "lucide-vue-next";
import {
  ContextMenuSub,
  ContextMenuSubTrigger,
  ContextMenuSubContent,
  ContextMenuItem,
  ContextMenuSeparator,
} from "@/components/ui/context-menu";
import { useAvailableTools, getToolsByCategory, type AvailableTool } from "@/composables/use-available-tools";
import { useOpenDirectory } from "@/composables/use-open-directory";
import { useOpenFile } from "@/composables/use-open-file";
import { useKeyFiles } from "@/composables/use-key-files";
import ToolIcon from "@/components/icons/ToolIcon.vue";

interface Props {
  directory: string;
}

const props = defineProps<Props>();

const { tools, isLoading: toolsLoading } = useAvailableTools();
const { openDirectory } = useOpenDirectory();
const { openFile } = useOpenFile();
const { filesByTool, isLoading: filesLoading, fetch: fetchKeyFiles } = useKeyFiles();

// Fetch key files whenever directory changes (lazy — only when this component mounts)
watch(
  () => props.directory,
  (dir) => {
    if (dir) void fetchKeyFiles(dir);
  },
  { immediate: true },
);

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

function toolKeyFiles(toolId: string): readonly string[] {
  return filesByTool.value[toolId] ?? [];
}

function hasKeyFiles(toolId: string): boolean {
  return toolKeyFiles(toolId).length > 0;
}

function handleOpenDirectory(toolId: string): void {
  void openDirectory(props.directory, toolId);
}

function handleOpenFile(filePath: string, toolId: string): void {
  // filePath is relative — build absolute path
  const absolute = [props.directory, filePath].join("/").replace(/\/\//g, "/");
  void openFile(absolute, toolId);
}
</script>

<template>
  <ContextMenuSub>
    <ContextMenuSubTrigger>
      <ExternalLink class="size-3.5" />
      Open in...
    </ContextMenuSubTrigger>
    <ContextMenuSubContent>
      <ContextMenuItem
        v-if="toolsLoading && tools.length === 0"
        disabled
      >
        <Loader2 class="size-3.5 animate-spin" />
        Detecting tools…
      </ContextMenuItem>
      <ContextMenuItem
        v-if="!toolsLoading && tools.length === 0"
        disabled
        class="text-muted-foreground"
      >
        No tools detected
      </ContextMenuItem>

      <template
        v-for="(group, gi) in groups"
        :key="group.category"
      >
        <ContextMenuSeparator v-if="gi > 0" />

        <!-- Terminals and explorers: always direct click, no key file concept -->
        <template v-if="group.category !== 'editor'">
          <ContextMenuItem
            v-for="tool in group.items"
            :key="tool.id"
            @click="handleOpenDirectory(tool.id)"
          >
            <ToolIcon :tool-id="tool.id" :size="14" />
            <span class="flex-1">{{ tool.label }}</span>
          </ContextMenuItem>
        </template>

        <!-- Editors: nested submenu if key files exist, direct click otherwise -->
        <template v-else>
          <template
            v-for="tool in group.items"
            :key="tool.id"
          >
            <!-- Tool has key files → nested submenu -->
            <ContextMenuSub v-if="hasKeyFiles(tool.id)">
              <ContextMenuSubTrigger>
                <ToolIcon :tool-id="tool.id" :size="14" />
                <span class="flex-1">{{ tool.label }}</span>
              </ContextMenuSubTrigger>
              <ContextMenuSubContent>
                <!-- Open directory entry -->
                <ContextMenuItem @click="handleOpenDirectory(tool.id)">
                  <Folder class="size-3.5" />
                  <span>Open directory</span>
                </ContextMenuItem>
                <ContextMenuSeparator />

                <!-- Spinner while key files are loading -->
                <ContextMenuItem v-if="filesLoading" disabled>
                  <Loader2 class="size-3.5 animate-spin" />
                  Loading…
                </ContextMenuItem>

                <!-- Key file entries -->
                <ContextMenuItem
                  v-for="filePath in toolKeyFiles(tool.id)"
                  :key="filePath"
                  @click="handleOpenFile(filePath, tool.id)"
                >
                  <FileText class="size-3.5" />
                  <span class="font-mono text-xs">{{ filePath }}</span>
                </ContextMenuItem>
              </ContextMenuSubContent>
            </ContextMenuSub>

            <!-- Tool has no key files → direct click -->
            <ContextMenuItem
              v-else
              @click="handleOpenDirectory(tool.id)"
            >
              <ToolIcon :tool-id="tool.id" :size="14" />
              <span class="flex-1">{{ tool.label }}</span>
            </ContextMenuItem>
          </template>
        </template>
      </template>
    </ContextMenuSubContent>
  </ContextMenuSub>
</template>
