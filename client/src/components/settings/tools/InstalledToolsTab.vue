<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { AlertCircle, LoaderCircle, Trash2, Wrench } from "lucide-vue-next";
import { useTools } from "@/composables/use-tools";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";

const { tools, isLoading, error, removeTool } = useTools();

const removingToolName = shallowRef<string | null>(null);
const confirmRemoveToolName = shallowRef<string | null>(null);

const hasTools = computed(() => tools.value.length > 0);

async function handleRemove(toolName: string): Promise<void> {
  removingToolName.value = toolName;
  try {
    await removeTool(toolName);
    confirmRemoveToolName.value = null;
  } finally {
    removingToolName.value = null;
  }
}

function confirmRemove(toolName: string): void {
  confirmRemoveToolName.value = toolName;
}

function cancelRemove(): void {
  confirmRemoveToolName.value = null;
}

function formatCommand(command: string | null | undefined, args: readonly string[] | null | undefined): string {
  if (!command) return "";
  const argsStr = args && args.length > 0 ? ` ${args.join(" ")}` : "";
  return `${command}${argsStr}`;
}
</script>

<template>
  <div class="space-y-4">
    <div
      v-if="isLoading"
      class="flex items-center gap-2 text-sm text-muted"
    >
      <LoaderCircle
        :size="16"
        class="animate-spin"
        aria-hidden="true"
      />
      <span>Loading tools…</span>
    </div>

    <div
      v-else-if="error && !hasTools"
      class="flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200"
      role="alert"
    >
      <AlertCircle
        :size="16"
        class="mt-0.5 shrink-0"
        aria-hidden="true"
      />
      <span>{{ error }}</span>
    </div>

    <div
      v-else-if="!hasTools"
      class="rounded-card border border-dashed border-border p-6 text-center"
    >
      <Wrench
        :size="28"
        class="mx-auto text-muted"
        aria-hidden="true"
      />
      <p class="mt-3 text-sm font-medium text-text">
        No tools installed
      </p>
      <p class="mt-1 text-xs text-muted">
        Install a tool from the Catalog or Custom tab.
      </p>
    </div>

    <div
      v-else
      class="grid gap-3"
    >
      <article
        v-for="tool in tools"
        :key="tool.name"
        class="rounded-card border border-border bg-main-bg p-4"
      >
        <div class="flex flex-col gap-4 md:flex-row md:items-start md:justify-between">
          <div class="min-w-0 flex-1">
            <div class="flex items-center gap-2">
              <Wrench
                :size="16"
                class="shrink-0 text-muted"
                aria-hidden="true"
              />
              <h3 class="truncate text-sm font-semibold text-text">
                {{ tool.displayName || tool.name }}
              </h3>
              <Badge
                v-if="tool.toolType === 'native'"
                variant="secondary"
              >
                Native
              </Badge>
              <Badge
                v-else-if="tool.toolType === 'mcp'"
                variant="default"
              >
                MCP
              </Badge>
            </div>

            <p
              v-if="tool.description"
              class="mt-2 text-sm text-muted"
            >
              {{ tool.description }}
            </p>

            <p
              v-if="tool.toolType === 'mcp' && tool.command"
              class="mt-3 break-all font-mono text-xs text-muted"
            >
              command: {{ formatCommand(tool.command, tool.args) }}
            </p>

            <p
              v-if="tool.toolType === 'native' && (tool.localPath || tool.repoUrl)"
              class="mt-3 break-all font-mono text-xs text-muted"
            >
              {{ tool.localPath || tool.repoUrl }}
            </p>
          </div>

          <div class="flex flex-wrap gap-2">
            <Button
              variant="outline"
              size="sm"
              class="text-red-400 hover:text-red-300 hover:border-red-500/50"
              :disabled="removingToolName === tool.name"
              @click="confirmRemoveToolName === tool.name ? handleRemove(tool.name) : confirmRemove(tool.name)"
            >
              <LoaderCircle
                v-if="removingToolName === tool.name"
                :size="16"
                class="animate-spin"
                aria-hidden="true"
              />
              <Trash2
                v-else
                :size="16"
                aria-hidden="true"
              />
              <span>
                {{ removingToolName === tool.name ? "Removing…" : confirmRemoveToolName === tool.name ? "Confirm Remove" : "Remove" }}
              </span>
            </Button>

            <Button
              v-if="confirmRemoveToolName === tool.name && removingToolName !== tool.name"
              variant="outline"
              size="sm"
              @click="cancelRemove"
            >
              Cancel
            </Button>
          </div>
        </div>
      </article>
    </div>

    <div
      v-if="error && hasTools"
      class="flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200"
      role="alert"
    >
      <AlertCircle
        :size="16"
        class="mt-0.5 shrink-0"
        aria-hidden="true"
      />
      <span>{{ error }}</span>
    </div>
  </div>
</template>
