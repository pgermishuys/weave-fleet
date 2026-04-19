<script setup lang="ts">
import { Pause, Play } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";

const queueStates = ["Running tasks", "Queued work", "Completed history"];
</script>

<template>
  <section class="flex h-full flex-col gap-6 overflow-auto p-6">
    <header class="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
      <div>
        <h1 class="text-2xl font-semibold tracking-tight text-foreground">Task Queue</h1>
        <p class="mt-1 text-sm text-muted-foreground">
          Queue controls are not wired to backend queue endpoints yet, so this page intentionally shows a placeholder instead of mock activity.
        </p>
      </div>

      <div class="flex items-center gap-2">
        <Button variant="outline" size="sm" disabled>
          <Play :size="14" />
          Resume
        </Button>
        <Button variant="outline" size="sm" disabled>
          <Pause :size="14" />
          Pause
        </Button>
      </div>
    </header>

    <Card class="border-dashed">
      <CardHeader class="gap-4">
        <div class="flex size-12 items-center justify-center rounded-full border border-border bg-muted/40 text-muted-foreground">
          <Pause :size="20" />
        </div>
        <div class="space-y-2">
          <CardTitle>Coming soon</CardTitle>
          <p class="text-sm text-muted-foreground">The live task queue will appear here once resume, pause, and status APIs are available.</p>
        </div>
      </CardHeader>

      <CardContent class="space-y-3">
        <p class="text-xs font-semibold uppercase tracking-[0.12em] text-muted-foreground">Planned sections</p>
        <ul class="grid gap-2 md:grid-cols-3">
          <li v-for="state in queueStates" :key="state" class="rounded-lg border border-border bg-muted/30 px-3 py-2 text-sm text-muted-foreground">
            {{ state }}
          </li>
        </ul>
      </CardContent>
    </Card>
  </section>
</template>
