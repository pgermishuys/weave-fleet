<script setup lang="ts">
import { useRouter } from "@tanstack/vue-router";
import { Button } from "@/components/ui/button";

const router = useRouter();

function goHome(): void {
  void router.navigate({ to: "/" });
}
</script>

<template>
  <section class="flex h-full flex-col items-center justify-center gap-4 p-6 text-center">
    <div class="space-y-2">
      <h1 class="text-2xl font-semibold tracking-tight text-foreground">Page Not Found</h1>
      <p class="text-sm text-muted-foreground">The page you&apos;re looking for doesn&apos;t exist in this workspace.</p>
    </div>

    <Button variant="outline" @click="goHome">Back to Fleet</Button>
  </section>
</template>
