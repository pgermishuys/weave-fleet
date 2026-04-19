<script setup lang="ts">
import { Card, CardContent } from "@/components/ui/card";

interface Props {
  label: string;
  value: number | string;
  secondary?: string;
  detail?: string;
}

withDefaults(defineProps<Props>(), {
  secondary: "",
  detail: "",
});
</script>

<template>
  <Card class="border-border/80 bg-card/70 py-0 backdrop-blur-sm">
    <CardContent class="space-y-4 px-5 py-4">
      <div class="space-y-1">
        <p class="text-xs font-medium uppercase tracking-[0.12em] text-muted-foreground">
          {{ label }}
        </p>

        <div class="flex flex-wrap items-end gap-x-3 gap-y-1">
          <p class="text-3xl font-semibold tracking-tight text-foreground">
            {{ value }}
          </p>

          <p
            v-if="secondary"
            class="text-sm font-medium text-muted-foreground"
          >
            {{ secondary }}
          </p>
        </div>
      </div>

      <p
        v-if="detail"
        class="text-sm leading-6 text-muted-foreground"
      >
        {{ detail }}
      </p>
    </CardContent>
  </Card>
</template>
