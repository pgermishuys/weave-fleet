<script setup lang="ts">
import { Variable } from "lucide-vue-next";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";

const upcomingTemplateCapabilities = [
  "Reusable prompt templates",
  "Variable-aware launch flows",
  "Template usage analytics",
];
</script>

<template>
  <section class="flex h-full flex-col gap-6 overflow-auto p-6">
    <header>
      <h1 class="text-2xl font-semibold tracking-tight text-foreground">Templates</h1>
      <p class="mt-1 text-sm text-muted-foreground">Template management is not connected to a backend yet, so this page intentionally shows a placeholder instead of mock data.</p>
    </header>

    <Card class="border-dashed">
      <CardHeader class="gap-4">
        <div class="flex size-12 items-center justify-center rounded-full border border-border bg-muted/40 text-muted-foreground">
          <Variable :size="20" />
        </div>
        <div class="space-y-2">
          <CardTitle>Coming soon</CardTitle>
          <p class="text-sm text-muted-foreground">We’ll enable saved templates here once the backing endpoints and launch flows are available.</p>
        </div>
      </CardHeader>

      <CardContent class="space-y-3">
        <p class="text-xs font-semibold uppercase tracking-[0.12em] text-muted-foreground">Planned in this view</p>
        <ul class="space-y-2 text-sm text-muted-foreground">
          <li v-for="capability in upcomingTemplateCapabilities" :key="capability" class="rounded-lg border border-border bg-muted/30 px-3 py-2">
            {{ capability }}
          </li>
        </ul>
      </CardContent>
    </Card>
  </section>
</template>
