<script setup lang="ts">
import { computed, onMounted, ref, shallowRef } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { FolderGit2, LoaderCircle, RefreshCw, Settings2, TriangleAlert } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { apiFetch } from "@/lib/api-client";
import type { RepositoryScanResponse, ScannedRepository } from "@/lib/api-types";

const router = useRouter();

const repositories = ref<ScannedRepository[]>([]);
const isLoading = shallowRef(true);
const isRefreshing = shallowRef(false);
const errorMessage = shallowRef<string | null>(null);
const scannedAt = shallowRef<number | null>(null);

const sortedRepositories = computed(() => {
  return [...repositories.value].sort((left, right) => left.name.localeCompare(right.name));
});

const scannedAtLabel = computed(() => {
  if (scannedAt.value === null) {
    return "Not scanned yet";
  }

  return new Date(scannedAt.value).toLocaleString();
});

onMounted(() => {
  void loadRepositories();
});

async function loadRepositories(forceRefresh = false): Promise<void> {
  errorMessage.value = null;

  if (forceRefresh) {
    isRefreshing.value = true;
  } else {
    isLoading.value = true;
  }

  try {
    if (forceRefresh) {
      const refreshResponse = await apiFetch("/api/repositories/refresh", { method: "POST" });

      if (!refreshResponse.ok) {
        throw new Error(`Refresh failed with HTTP ${refreshResponse.status}`);
      }
    }

    const response = await apiFetch("/api/repositories");

    if (!response.ok) {
      throw new Error(`Unable to load repositories (HTTP ${response.status})`);
    }

    const data = (await response.json()) as RepositoryScanResponse;
    repositories.value = data.repositories;
    scannedAt.value = data.scannedAt;
  } catch (error) {
    repositories.value = [];
    scannedAt.value = null;
    errorMessage.value = error instanceof Error ? error.message : "Unable to load repositories.";
  } finally {
    isLoading.value = false;
    isRefreshing.value = false;
  }
}

function goToSettings(): void {
  void router.navigate({ to: "/settings" });
}
</script>

<template>
  <section class="flex h-full flex-col gap-6 overflow-auto p-6">
    <header class="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
      <div>
        <h1 class="text-2xl font-semibold tracking-tight text-foreground">Repositories</h1>
        <p class="mt-1 text-sm text-muted-foreground">Browse local git repositories discovered from configured workspace roots.</p>
      </div>

      <div class="flex flex-wrap items-center gap-3">
        <span class="text-xs text-muted-foreground">Last scan: {{ scannedAtLabel }}</span>

        <Button variant="outline" size="sm" :disabled="isLoading || isRefreshing" @click="loadRepositories(true)">
          <RefreshCw :size="14" :class="isRefreshing ? 'animate-spin' : ''" />
          Refresh
        </Button>

        <Button variant="outline" size="sm" @click="goToSettings">
          <Settings2 :size="14" />
          Workspace settings
        </Button>
      </div>
    </header>

    <div v-if="isLoading" class="flex flex-1 items-center justify-center gap-3 rounded-xl border border-border bg-card p-8 text-sm text-muted-foreground">
      <LoaderCircle :size="18" class="animate-spin" />
      <span>Loading repositories…</span>
    </div>

    <div
      v-else-if="errorMessage"
      class="flex items-start gap-3 rounded-xl border border-red-500/30 bg-red-500/10 p-4 text-sm text-red-200"
      role="alert"
    >
      <TriangleAlert :size="18" class="mt-0.5 shrink-0" />
      <div class="space-y-2">
        <p class="font-medium">Unable to load repositories</p>
        <p>{{ errorMessage }}</p>
      </div>
    </div>

    <div v-else-if="sortedRepositories.length === 0" class="flex flex-1 flex-col items-center justify-center gap-4 rounded-xl border border-dashed border-border bg-card/40 p-8 text-center">
      <FolderGit2 :size="44" class="text-muted-foreground/50" />
      <div class="space-y-1">
        <p class="text-sm font-medium text-foreground">No repositories found</p>
        <p class="text-sm text-muted-foreground">Add workspace roots in Settings to enable repository discovery.</p>
      </div>
      <Button variant="outline" size="sm" @click="goToSettings">
        <Settings2 :size="14" />
        Open settings
      </Button>
    </div>

    <div v-else class="grid gap-4 xl:grid-cols-2">
      <Card v-for="repository in sortedRepositories" :key="repository.path" class="gap-4 py-5">
        <CardHeader class="gap-2 px-5">
          <div class="flex items-start gap-3">
            <div class="rounded-lg border border-border bg-muted/30 p-2 text-muted-foreground">
              <FolderGit2 :size="16" />
            </div>
            <div class="min-w-0 flex-1">
              <CardTitle class="truncate text-base">{{ repository.name }}</CardTitle>
              <p class="mt-1 truncate text-xs text-muted-foreground">{{ repository.path }}</p>
            </div>
          </div>
        </CardHeader>

        <CardContent class="px-5 pt-0">
          <dl class="grid gap-2 text-sm sm:grid-cols-[auto_1fr] sm:items-start">
            <dt class="text-muted-foreground">Workspace root</dt>
            <dd class="break-all text-foreground">{{ repository.parentRoot }}</dd>
          </dl>
        </CardContent>
      </Card>
    </div>
  </section>
</template>
