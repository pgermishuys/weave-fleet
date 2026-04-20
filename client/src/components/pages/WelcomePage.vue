<script setup lang="ts">
import weaveLogo from "@/assets/weave_logo.png";

const appVersion = import.meta.env.VITE_APP_VERSION;
const commitSha = import.meta.env.VITE_COMMIT_SHA;
</script>

<template>
  <section class="flex h-full items-center justify-center p-6">
    <div class="flex max-w-md flex-col items-center gap-4 text-center">
      <div class="flex h-18 w-18 items-center justify-center rounded-card bg-accent p-3 shadow-sm">
        <img
          :src="weaveLogo"
          alt="Weave logo"
          class="logo-mark"
        >
      </div>

      <div>
        <h1 class="weave-gradient-text text-3xl font-bold tracking-tight">
          Weave
        </h1>
        <p class="font-mono text-sm text-muted-foreground">
          Agent Fleet
        </p>
      </div>

      <p class="text-sm leading-relaxed text-muted-foreground">
        Welcome to Weave Agent Fleet. Select a view from the workspace shell to get started, or open a session to continue recent work.
      </p>

      <p class="text-xs text-muted-foreground/60">
        v{{ appVersion }} · {{ commitSha }}
      </p>
    </div>
  </section>
</template>

<style scoped>
.logo-mark {
  width: 100%;
  height: 100%;
  object-fit: contain;
}

.shadow-sm {
  box-shadow: 0 12px 32px rgba(0, 0, 0, 0.2);
}
</style>
