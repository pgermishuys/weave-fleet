<script setup lang="ts">
import { Clock3, Coins, Hash } from "lucide-vue-next";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import { formatCost, formatDuration, formatTokens, getStatusDot } from "@/lib/format-utils";
import type { QueueItem } from "@/lib/types";

defineProps<{
  item: QueueItem;
}>();
</script>

<template>
  <Card>
    <CardContent class="flex flex-col gap-4 px-4 py-4 sm:flex-row sm:items-center">
      <div class="flex min-w-0 flex-1 items-start gap-3">
        <span
          class="mt-1 h-2.5 w-2.5 shrink-0 rounded-full"
          :class="[getStatusDot(item.status), item.status === 'running' ? 'animate-pulse' : '']"
        />

        <div class="min-w-0 flex-1">
          <p class="truncate text-sm font-medium text-foreground">{{ item.prompt }}</p>
          <div class="mt-1 flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
            <span class="font-mono">{{ item.workspaceDir }}</span>
            <Badge variant="outline" class="px-1.5 py-0 text-[10px]">P{{ item.priority }}</Badge>
          </div>
        </div>
      </div>

      <div class="flex flex-wrap items-center gap-3 text-xs text-muted-foreground">
        <span v-if="item.tokens != null" class="inline-flex items-center gap-1">
          <Hash :size="12" /> {{ formatTokens(item.tokens) }}
        </span>
        <span v-if="item.cost != null" class="inline-flex items-center gap-1">
          <Coins :size="12" /> {{ formatCost(item.cost) }}
        </span>
        <span v-if="item.duration != null" class="inline-flex items-center gap-1">
          <Clock3 :size="12" /> {{ formatDuration(item.duration) }}
        </span>
      </div>
    </CardContent>
  </Card>
</template>
