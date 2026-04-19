<script setup lang="ts">
import { computed, onMounted, shallowRef } from "vue";
import weaveLogo from "@/assets/weave_logo.png";
import { apiUrl } from "@/lib/api-client";

const returnUrl = shallowRef("/");

const signInHref = computed(() => apiUrl(`/auth/login?returnUrl=${encodeURIComponent(returnUrl.value)}`));
const buildLabel = computed(() => `v${import.meta.env.VITE_APP_VERSION} · ${import.meta.env.VITE_COMMIT_SHA}`);

onMounted(() => {
  returnUrl.value = resolveReturnUrl(window.location.search);
});

function resolveReturnUrl(search: string): string {
  const param = new URLSearchParams(search).get("returnUrl");

  if (param === null || !isSafeReturnUrl(param)) {
    return "/";
  }

  return param;
}

function isSafeReturnUrl(value: string): boolean {
  return value.startsWith("/") && !value.startsWith("//");
}
</script>

<template>
  <main class="flex min-h-screen items-center justify-center bg-main-bg px-4 py-12">
    <section class="w-full max-w-sm text-center">
      <div class="flex flex-col items-center gap-6 rounded-card border border-border bg-card-bg px-8 py-10 shadow-sm">
        <div class="flex h-18 w-18 items-center justify-center rounded-card bg-accent p-3 shadow-sm">
          <img :src="weaveLogo" alt="Weave logo" class="logo-mark" />
        </div>

        <div>
          <h1 class="font-mono text-3xl font-bold tracking-tight text-text">Weave</h1>
          <p class="mt-1 font-mono text-sm text-muted">Agent Fleet</p>
        </div>

        <p class="text-sm leading-relaxed text-muted">
          Orchestrate AI coding agents across your projects.
        </p>

        <div class="flex w-full flex-col gap-3">
          <a
            :href="signInHref"
            class="inline-flex items-center justify-center rounded-btn bg-primary px-6 py-2.5 text-sm font-medium text-white transition-opacity hover:opacity-90"
          >
            Sign in
          </a>

          <a
            :href="signInHref"
            class="inline-flex items-center justify-center rounded-btn border border-border bg-main-bg px-6 py-2.5 text-sm font-medium text-text transition-colors hover:border-accent/50"
          >
            Sign up
          </a>
        </div>

        <p class="text-[10px] text-muted/70">
          {{ buildLabel }}
        </p>
      </div>
    </section>
  </main>
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
