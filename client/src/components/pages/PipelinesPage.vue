<script setup lang="ts">
import { GitBranch } from "lucide-vue-next";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";

const pipelineStages = ["Authoring", "Review", "Execution", "Reporting"];
</script>

<template>
  <section class="flex h-full flex-col gap-6 overflow-auto p-6">
    <header>
      <h1 class="text-2xl font-semibold tracking-tight text-foreground">Pipelines</h1>
      <p class="mt-1 text-sm text-muted-foreground">Pipeline definitions are not backed by an API yet, so this page intentionally shows a placeholder instead of mock workflows.</p>
    </header>

    <Card class="border-dashed">
      <CardHeader class="gap-4">
        <div class="flex size-12 items-center justify-center rounded-full border border-border bg-muted/40 text-muted-foreground">
          <GitBranch :size="20" />
        </div>
        <div class="space-y-2">
          <CardTitle>Coming soon</CardTitle>
          <p class="text-sm text-muted-foreground">This view will list saved pipelines after the workflow orchestration endpoints are implemented.</p>
        </div>
      </CardHeader>

      <CardContent class="space-y-3">
        <p class="text-xs font-semibold uppercase tracking-[0.12em] text-muted-foreground">Expected lifecycle</p>
        <ol class="grid gap-2 md:grid-cols-2 xl:grid-cols-4">
          <li v-for="stage in pipelineStages" :key="stage" class="rounded-lg border border-border bg-muted/30 px-3 py-2 text-sm text-muted-foreground">
            {{ stage }}
          </li>
        </ol>
      </CardContent>
    </Card>
  </section>
</template>
