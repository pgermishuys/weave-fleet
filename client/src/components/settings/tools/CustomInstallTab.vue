<script setup lang="ts">
import { shallowRef, computed } from "vue";
import { AlertCircle, Download, LoaderCircle } from "lucide-vue-next";
import { useTools, type InstallToolRequest } from "@/composables/use-tools";
import { Button } from "@/components/ui/button";

const { installTool } = useTools();

type InstallMode = "native" | "mcp";

const installMode = shallowRef<InstallMode>("native");
const isInstalling = shallowRef(false);
const formError = shallowRef<string | null>(null);

// Native mode fields
const nativeUrl = shallowRef("");

// MCP mode fields
const mcpName = shallowRef("");
const mcpCommand = shallowRef("");
const mcpArgs = shallowRef("");
const mcpEnv = shallowRef("");

const isNativeMode = computed(() => installMode.value === "native");
const isMcpMode = computed(() => installMode.value === "mcp");

function setMode(mode: InstallMode): void {
  installMode.value = mode;
  formError.value = null;
}

function isGitHubUrl(url: string): boolean {
  return url.startsWith("https://github.com/") || url.startsWith("http://github.com/");
}

function extractNameFromUrl(url: string): string {
  const trimmed = url.trim().replace(/\/$/, "");
  const parts = trimmed.split("/");
  return parts[parts.length - 1] || "tool";
}

function parseArgs(argsText: string): string[] {
  return argsText
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

function parseEnv(envText: string): Record<string, string> {
  const env: Record<string, string> = {};
  const lines = envText
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.length > 0);

  for (const line of lines) {
    const eqIndex = line.indexOf("=");
    if (eqIndex > 0) {
      const key = line.substring(0, eqIndex).trim();
      const value = line.substring(eqIndex + 1).trim();
      if (key) {
        env[key] = value;
      }
    }
  }

  return env;
}

async function submitNativeInstall(): Promise<void> {
  const url = nativeUrl.value.trim();
  if (!url) {
    formError.value = "Tool URL or path is required.";
    return;
  }

  isInstalling.value = true;
  formError.value = null;

  try {
    const isGitHub = isGitHubUrl(url);
    const name = extractNameFromUrl(url);

    const request: InstallToolRequest = {
      name,
      toolType: "native",
      source: isGitHub ? 1 : 2, // 1=GitHub, 2=Local
      command: null,
      args: null,
      env: null,
      repoUrl: isGitHub ? url : null,
      localPath: isGitHub ? null : url,
    };

    await installTool(request);
    nativeUrl.value = "";
  } catch (installError) {
    formError.value = installError instanceof Error ? installError.message : "Failed to install tool.";
  } finally {
    isInstalling.value = false;
  }
}

async function submitMcpInstall(): Promise<void> {
  const name = mcpName.value.trim();
  const command = mcpCommand.value.trim();

  if (!name) {
    formError.value = "Name is required.";
    return;
  }

  if (!command) {
    formError.value = "Command is required.";
    return;
  }

  isInstalling.value = true;
  formError.value = null;

  try {
    const args = parseArgs(mcpArgs.value);
    const env = parseEnv(mcpEnv.value);

    const request: InstallToolRequest = {
      name,
      toolType: "mcp",
      source: 2, // Local
      command,
      args: args.length > 0 ? args : null,
      env: Object.keys(env).length > 0 ? env : null,
      repoUrl: null,
      localPath: null,
    };

    await installTool(request);
    mcpName.value = "";
    mcpCommand.value = "";
    mcpArgs.value = "";
    mcpEnv.value = "";
  } catch (installError) {
    formError.value = installError instanceof Error ? installError.message : "Failed to install MCP server.";
  } finally {
    isInstalling.value = false;
  }
}

async function submitInstall(): Promise<void> {
  if (isNativeMode.value) {
    await submitNativeInstall();
  } else {
    await submitMcpInstall();
  }
}
</script>

<template>
  <div class="space-y-4">
    <!-- Mode toggle -->
    <div class="flex gap-2 rounded-card border border-border bg-card-bg p-1">
      <button
        type="button"
        class="flex-1 rounded-btn px-4 py-2 text-sm font-medium transition-colors"
        :class="
          isNativeMode
            ? 'bg-accent text-accent-fg'
            : 'text-muted hover:text-text'
        "
        :disabled="isInstalling"
        @click="setMode('native')"
      >
        Native
      </button>
      <button
        type="button"
        class="flex-1 rounded-btn px-4 py-2 text-sm font-medium transition-colors"
        :class="
          isMcpMode
            ? 'bg-accent text-accent-fg'
            : 'text-muted hover:text-text'
        "
        :disabled="isInstalling"
        @click="setMode('mcp')"
      >
        MCP
      </button>
    </div>

    <!-- Native mode -->
    <div
      v-if="isNativeMode"
      class="rounded-card border border-border bg-main-bg p-4"
    >
      <form
        class="space-y-3"
        @submit.prevent="submitInstall"
      >
        <div>
          <h3 class="text-sm font-semibold text-text">
            Install from URL or path
          </h3>
          <p class="mt-1 text-xs text-muted">
            Install a native tool from a GitHub URL or local file path.
          </p>
        </div>

        <label class="grid gap-1 text-sm text-text">
          <span class="text-xs font-medium uppercase tracking-wide text-muted">Tool URL or Path</span>
          <input
            v-model="nativeUrl"
            type="text"
            class="w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent"
            placeholder="https://github.com/user/tool or /path/to/tool"
            :disabled="isInstalling"
          >
        </label>

        <div
          v-if="formError"
          class="flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200"
          role="alert"
        >
          <AlertCircle
            :size="16"
            class="mt-0.5 shrink-0"
            aria-hidden="true"
          />
          <span>{{ formError }}</span>
        </div>

        <Button
          type="submit"
          variant="default"
          :disabled="isInstalling"
        >
          <LoaderCircle
            v-if="isInstalling"
            :size="16"
            class="animate-spin"
            aria-hidden="true"
          />
          <Download
            v-else
            :size="16"
            aria-hidden="true"
          />
          <span>{{ isInstalling ? "Installing…" : "Install Tool" }}</span>
        </Button>
      </form>
    </div>

    <!-- MCP mode -->
    <div
      v-if="isMcpMode"
      class="rounded-card border border-border bg-main-bg p-4"
    >
      <form
        class="space-y-3"
        @submit.prevent="submitInstall"
      >
        <div>
          <h3 class="text-sm font-semibold text-text">
            Configure MCP Server
          </h3>
          <p class="mt-1 text-xs text-muted">
            Install an MCP server with custom command, arguments, and environment variables.
          </p>
        </div>

        <label class="grid gap-1 text-sm text-text">
          <span class="text-xs font-medium uppercase tracking-wide text-muted">Name</span>
          <input
            v-model="mcpName"
            type="text"
            class="w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent"
            placeholder="my-mcp-server"
            :disabled="isInstalling"
          >
        </label>

        <label class="grid gap-1 text-sm text-text">
          <span class="text-xs font-medium uppercase tracking-wide text-muted">Command</span>
          <input
            v-model="mcpCommand"
            type="text"
            class="w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent"
            placeholder="node"
            :disabled="isInstalling"
          >
        </label>

        <label class="grid gap-1 text-sm text-text">
          <span class="text-xs font-medium uppercase tracking-wide text-muted">Arguments (one per line)</span>
          <textarea
            v-model="mcpArgs"
            rows="4"
            class="w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent"
            placeholder="/path/to/server.js&#10;--port&#10;3000"
            :disabled="isInstalling"
          />
        </label>

        <label class="grid gap-1 text-sm text-text">
          <span class="text-xs font-medium uppercase tracking-wide text-muted">Environment Variables (KEY=VALUE per line)</span>
          <textarea
            v-model="mcpEnv"
            rows="4"
            class="w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent"
            placeholder="API_KEY=your-key&#10;DEBUG=true"
            :disabled="isInstalling"
          />
        </label>

        <div
          v-if="formError"
          class="flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200"
          role="alert"
        >
          <AlertCircle
            :size="16"
            class="mt-0.5 shrink-0"
            aria-hidden="true"
          />
          <span>{{ formError }}</span>
        </div>

        <Button
          type="submit"
          variant="default"
          :disabled="isInstalling"
        >
          <LoaderCircle
            v-if="isInstalling"
            :size="16"
            class="animate-spin"
            aria-hidden="true"
          />
          <Download
            v-else
            :size="16"
            aria-hidden="true"
          />
          <span>{{ isInstalling ? "Installing…" : "Install MCP Server" }}</span>
        </Button>
      </form>
    </div>

    <!-- Examples -->
    <div class="rounded-card border border-border bg-card-bg p-4">
      <h4 class="text-sm font-semibold text-text">
        Examples
      </h4>
      <div
        v-if="isNativeMode"
        class="mt-2 space-y-1 text-xs text-muted"
      >
        <p class="font-semibold text-text">
          Native tools:
        </p>
        <ul class="space-y-1">
          <li class="font-mono">
            https://github.com/username/tool-name
          </li>
          <li class="font-mono">
            /Users/username/.config/opencode/tools/my-tool
          </li>
          <li class="font-mono">
            ~/tools/custom-tool
          </li>
        </ul>
      </div>
      <div
        v-if="isMcpMode"
        class="mt-2 space-y-2 text-xs text-muted"
      >
        <div>
          <p class="font-semibold text-text">
            MCP server example:
          </p>
          <ul class="mt-1 space-y-1">
            <li>
              <span class="font-semibold">Name:</span> <span class="font-mono">filesystem</span>
            </li>
            <li>
              <span class="font-semibold">Command:</span> <span class="font-mono">npx</span>
            </li>
            <li>
              <span class="font-semibold">Args:</span> <span class="font-mono">-y @modelcontextprotocol/server-filesystem /path/to/allowed/files</span>
            </li>
          </ul>
        </div>
      </div>
    </div>
  </div>
</template>
