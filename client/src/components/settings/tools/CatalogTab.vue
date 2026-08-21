<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { AlertCircle, Wrench, Download, LoaderCircle } from "lucide-vue-next";
import { useToolCatalog } from "@/composables/use-tool-catalog";
import { useTools } from "@/composables/use-tools";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";

interface InstallToolRequest {
  name: string;
  toolType: string;
  source: number; // SkillSource enum: 0=Bundled, 1=GitHub, 2=Local
  command: null | string;
  args: null | string[];
  env: null | Record<string, string>;
  repoUrl: null | string;
  localPath: null | string;
}

const { catalog, isLoading, error, installTool } = useToolCatalog();
const { tools: installedTools, fetchTools } = useTools();

const installingToolName = shallowRef<string | null>(null);

const hasCatalog = computed(() => catalog.value.length > 0);

function isInstalled(toolName: string): boolean {
  return installedTools.value.some((t) => t.name === toolName);
}

async function handleInstall(toolName: string): Promise<void> {
  const catalogEntry = catalog.value.find((t) => t.name === toolName);
  if (!catalogEntry) {
    return;
  }

  installingToolName.value = toolName;

  try {
    const request: InstallToolRequest = {
      name: catalogEntry.name,
      toolType: catalogEntry.toolType,
      source: 1, // Default to GitHub (1) for catalog installs
      command: catalogEntry.command ?? null,
      args: catalogEntry.args ? [...catalogEntry.args] : null,
      env: catalogEntry.env ?? null,
      repoUrl: catalogEntry.repoUrl ?? null,
      localPath: catalogEntry.localPath ?? null,
    };

    await installTool(request);
    await fetchTools();
  } finally {
    installingToolName.value = null;
  }
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
      <span>Loading catalog…</span>
    </div>

    <div
      v-else-if="error && !hasCatalog"
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
      v-else-if="!hasCatalog"
      class="rounded-card border border-dashed border-border p-6 text-center"
    >
      <Wrench
        :size="28"
        class="mx-auto text-muted"
        aria-hidden="true"
      />
      <p class="mt-3 text-sm font-medium text-text">
        No tools in catalog
      </p>
      <p class="mt-1 text-xs text-muted">
        The tool catalog is empty or unavailable.
      </p>
    </div>

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
              <LoaderCircle
                v-if="installingToolName === tool.name"
                :size="16"
                class="animate-spin"
                aria-hidden="true"
              />
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

    <div
      v-if="error && hasCatalog"
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
