<script setup lang="ts">
import { formatDateTime } from "@/lib/format";
import { computed, onMounted, ref, shallowRef } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { FolderGit2, RefreshCw, Settings2 } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { api } from "@/api/client";
import { useSettingsNav } from "@/composables/use-settings-nav";
import type { RepositoryScanResponse, ScannedRepository } from "@/api/client";
import { EmptyState } from "@/components/ui/empty-state";
import { ErrorNote } from "@/components/ui/error-note";
import { PageHeader } from "@/components/ui/page-header";
import { Spinner } from "@/components/ui/spinner";

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

  return formatDateTime(scannedAt.value);
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
      const { error: refreshError } = await api.POST("/api/repositories/refresh", {});

      if (refreshError) {
        throw new Error(String(refreshError));
      }
    }

    const { data, error: apiError } = await api.GET("/api/repositories", {});

    if (apiError) {
      throw new Error(String(apiError));
    }

    if (!data) {
      throw new Error("No data returned");
    }

    const responseData = data as RepositoryScanResponse;
    repositories.value = responseData.repositories;
    scannedAt.value = responseData.scannedAt;
  } catch (error) {
    repositories.value = [];
    scannedAt.value = null;
    errorMessage.value = error instanceof Error ? error.message : "Unable to load repositories.";
  } finally {
    isLoading.value = false;
    isRefreshing.value = false;
  }
}

const { setActiveSection } = useSettingsNav();

function goToSettings(): void {
  setActiveSection("folders");
  void router.navigate({ to: "/settings" });
}
</script>

<template>
  <section class="flex h-full flex-col gap-6 overflow-auto p-6">
    <PageHeader
      title="Repositories"
      description="Git repositories Fleet found in your locations (Settings → Folders)."
    >
      <template #actions>
        <span class="text-xs text-muted-foreground">Last scan: {{ scannedAtLabel }}</span>

        <Button
          variant="outline"
          size="sm"
          :disabled="isLoading || isRefreshing"
          @click="loadRepositories(true)"
        >
          <Spinner
            v-if="isRefreshing"
            size="sm"
          />
          <RefreshCw
            v-else
            :size="14"
          />
          Refresh
        </Button>

        <Button
          variant="outline"
          size="sm"
          @click="goToSettings"
        >
          <Settings2 :size="14" />
          Workspace settings
        </Button>
      </template>
    </PageHeader>

    <div
      v-if="isLoading"
      class="flex flex-1 items-center justify-center gap-3 border border-border bg-card p-8 text-sm text-muted-foreground"
    >
      <Spinner size="lg" />
      <span>Loading repositories…</span>
    </div>

    <ErrorNote
      v-else-if="errorMessage"
      message="Unable to load repositories"
      :reason="errorMessage"
    />

    <EmptyState
      v-else-if="sortedRepositories.length === 0"
      :icon="FolderGit2"
      title="No repositories found"
      description="Add a location in Settings → Folders, and Fleet lists the repositories inside it."
      bordered
      class="flex-1"
    >
      <template #action>
        <Button
          variant="outline"
          size="sm"
          @click="goToSettings"
        >
          <Settings2 :size="14" />
          Open settings
        </Button>
      </template>
    </EmptyState>

    <div
      v-else
      class="grid grid-cols-1 gap-4 xl:grid-cols-2"
    >
      <Card
        v-for="repository in sortedRepositories"
        :key="repository.path"
        class="gap-4 py-5"
      >
        <CardHeader class="gap-2 px-5">
          <div class="flex min-w-0 items-start gap-3">
            <div class="border border-border bg-muted/30 p-2 text-muted-foreground">
              <FolderGit2 :size="16" />
            </div>
            <div class="min-w-0 flex-1">
              <CardTitle class="truncate text-base">
                {{ repository.name }}
              </CardTitle>
              <p class="mt-1 truncate text-xs text-muted-foreground">
                {{ repository.path }}
              </p>
            </div>
          </div>
        </CardHeader>

        <CardContent class="px-5 pt-0">
          <dl class="grid gap-2 text-sm sm:grid-cols-[auto_1fr] sm:items-start">
            <dt class="text-muted-foreground">
              Workspace root
            </dt>
            <dd class="break-all text-foreground">
              {{ repository.parentRoot }}
            </dd>
          </dl>
        </CardContent>
      </Card>
    </div>
  </section>
</template>
