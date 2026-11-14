<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { Wrench, Download } from "lucide-vue-next";
import { useToolCatalog } from "@/composables/use-tool-catalog";
import { useTools } from "@/composables/use-tools";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import type { InstallToolRequest } from "@/composables/use-tool-catalog";
import { isSameTarget, targetBody, type InstallTarget } from "@/lib/install-target";
import InstallTargetPicker from "../InstallTargetPicker.vue";
import { EmptyState } from "@/components/ui/empty-state";
import { ErrorNote } from "@/components/ui/error-note";
import { Spinner } from "@/components/ui/spinner";

const target = defineModel<InstallTarget>("target", { required: true });

const { catalog, isLoading, error, installTool } = useToolCatalog();
const { tools: installedTools, fetchTools } = useTools();

const installingToolName = shallowRef<string | null>(null);
const installError = shallowRef<string | null>(null);

const hasCatalog = computed(() => catalog.value.length > 0);

/** Installed where the picker points: a tool can also be installed elsewhere. */
function isInstalled(toolName: string): boolean {
  return installedTools.value.some((t) => t.name === toolName && isSameTarget(t.target, target.value));
}

async function handleInstall(toolName: string): Promise<void> {
  const catalogEntry = catalog.value.find((t) => t.name === toolName);
  if (!catalogEntry) {
    return;
  }

  installingToolName.value = toolName;
  installError.value = null;

  try {
    const request: InstallToolRequest = {
      name: catalogEntry.name,
      toolType: catalogEntry.toolType,
      source: catalogEntry.source,
      command: catalogEntry.command ?? null,
      args: catalogEntry.args ? [...catalogEntry.args] : null,
      env: catalogEntry.env ?? null,
      repoUrl: catalogEntry.repoUrl ?? null,
      ref: catalogEntry.ref ?? null,
      subPath: catalogEntry.subPath ?? null,
      localPath: catalogEntry.localPath ?? null,
      ...targetBody(target.value),
    };

    await installTool(request);
    await fetchTools();
  } catch (err) {
    installError.value = err instanceof Error ? err.message : "Failed to install tool.";
  } finally {
    installingToolName.value = null;
  }
}
</script>

<template>
  <div class="space-y-4">
    <InstallTargetPicker
      v-model="target"
      kind="tools"
      :disabled="installingToolName !== null"
    />

    <div
      v-if="isLoading"
      class="flex items-center gap-2 text-sm text-muted"
    >
      <Spinner />
      <span>Loading catalog…</span>
    </div>

    <ErrorNote
      v-else-if="error && !hasCatalog"
      :message="error"
    />

    <EmptyState
      v-else-if="!hasCatalog"
      :icon="Wrench"
      title="No tools in catalog"
      description="The tool catalog is empty or unavailable."
      bordered
    />

    <div
      v-else
      class="grid gap-3 sm:grid-cols-2"
    >
      <article
        v-for="tool in catalog"
        :key="tool.name"
        class="rounded-card border border-border bg-main-bg p-4"
      >
        <div class="flex flex-col gap-3">
          <div class="flex items-start gap-2">
            <Wrench
              :size="16"
              class="mt-0.5 shrink-0 text-muted"
              aria-hidden="true"
            />
            <div class="min-w-0 flex-1">
              <h3 class="truncate text-sm font-semibold text-text">
                {{ tool.displayName || tool.name }}
              </h3>
              <p
                v-if="tool.author"
                class="mt-0.5 text-xs text-muted"
              >
                by {{ tool.author }}
              </p>
            </div>
          </div>

          <p
            v-if="tool.description"
            class="text-sm text-muted line-clamp-3"
          >
            {{ tool.description }}
          </p>

          <div
            v-if="tool.tags.length > 0"
            class="flex flex-wrap gap-1"
          >
            <Badge
              v-for="tag in tool.tags"
              :key="tag"
              variant="outline"
              class="text-xs"
            >
              {{ tag }}
            </Badge>
          </div>

          <div class="flex items-center justify-between gap-2 pt-2">
            <div class="flex items-center gap-2">
              <Badge
                v-if="tool.toolType === 'native'"
                variant="secondary"
                class="text-xs"
              >
                Native
              </Badge>
              <Badge
                v-else-if="tool.toolType === 'mcp'"
                variant="secondary"
                class="text-xs"
              >
                MCP
              </Badge>
              <span
                v-if="tool.version"
                class="text-xs text-muted"
              >
                v{{ tool.version }}
              </span>
            </div>

            <Button
              v-if="isInstalled(tool.name)"
              variant="outline"
              size="sm"
              disabled
            >
              Installed
            </Button>
            <Button
              v-else
              variant="default"
              size="sm"
              :disabled="installingToolName === tool.name"
              @click="handleInstall(tool.name)"
            >
              <Spinner v-if="installingToolName === tool.name" />
              <Download
                v-else
                :size="16"
                aria-hidden="true"
              />
              <span>{{ installingToolName === tool.name ? "Installing…" : "Install" }}</span>
            </Button>
          </div>
        </div>
      </article>
    </div>

    <ErrorNote
      v-if="installError"
      :message="installError"
    />

    <ErrorNote
      v-else-if="error && hasCatalog"
      :message="error"
    />
  </div>
</template>
