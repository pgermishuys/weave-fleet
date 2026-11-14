<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { AlertTriangle, LoaderCircle, Trash2, Wrench } from "lucide-vue-next";
import { useTools, type ToolRef } from "@/composables/use-tools";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { targetKey, targetLabel } from "@/lib/install-target";
import { EmptyState } from "@/components/ui/empty-state";
import { ErrorNote } from "@/components/ui/error-note";

const { tools, isLoading, error, removeTool } = useTools();

// Busy states are per install: the same tool can be installed globally and in repositories.
const removingKey = shallowRef<string | null>(null);
const confirmRemoveKey = shallowRef<string | null>(null);

const hasTools = computed(() => tools.value.length > 0);

function keyOf(tool: ToolRef): string {
  return `${tool.name}|${targetKey(tool.target)}`;
}

async function handleRemove(tool: ToolRef): Promise<void> {
  removingKey.value = keyOf(tool);
  try {
    await removeTool(tool);
    confirmRemoveKey.value = null;
  } catch {
    // The composable's error shows below the list.
  } finally {
    removingKey.value = null;
  }
}

function confirmRemove(tool: ToolRef): void {
  confirmRemoveKey.value = keyOf(tool);
}

function cancelRemove(): void {
  confirmRemoveKey.value = null;
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

    <ErrorNote
      v-else-if="error && !hasTools"
      :message="error"
    />

    <EmptyState
      v-else-if="!hasTools"
      :icon="Wrench"
      title="No tools installed"
      description="Install a tool from the Catalog or Custom tab."
      bordered
    />

    <div
      v-else
      class="grid gap-3"
    >
      <article
        v-for="tool in tools"
        :key="keyOf(tool)"
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
              <Badge
                variant="outline"
                :title="tool.target.scope === 'project' ? tool.target.projectPath : 'Every session'"
              >
                {{ targetLabel(tool.target) }}
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
            <p
              v-if="tool.installedPath"
              class="mt-1 break-all font-mono text-xs text-muted"
            >
              → {{ tool.installedPath }}
            </p>

            <div
              v-else
              class="mt-2 flex items-start gap-2 rounded-card border border-coral/30 bg-coral/10 px-2 py-1 text-xs text-coral"
            >
              <AlertTriangle
                :size="12"
                class="mt-0.5 shrink-0"
                aria-hidden="true"
              />
              <span>Not anywhere OpenCode looks. Remove it and install it again.</span>
            </div>
          </div>

          <div class="flex flex-wrap gap-2">
            <Button
              variant="outline"
              size="sm"
              class="text-error-text hover:text-error-text hover:border-error/50"
              :disabled="removingKey === keyOf(tool)"
              @click="confirmRemoveKey === keyOf(tool) ? handleRemove(tool) : confirmRemove(tool)"
            >
              <LoaderCircle
                v-if="removingKey === keyOf(tool)"
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
                {{ removingKey === keyOf(tool) ? "Removing…" : confirmRemoveKey === keyOf(tool) ? "Confirm Remove" : "Remove" }}
              </span>
            </Button>

            <Button
              v-if="confirmRemoveKey === keyOf(tool) && removingKey !== keyOf(tool)"
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

    <ErrorNote
      v-if="error && hasTools"
      :message="error"
    />
  </div>
</template>
