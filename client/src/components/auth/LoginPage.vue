<script setup lang="ts">
import { computed, onMounted, onUnmounted, shallowRef } from "vue";
import weaveLogo from "@/assets/weave_logo.png";
import { apiFetch, apiUrl } from "@/lib/api-client";

interface AuthStatusResponse {
  authEnabled?: boolean;
  tokenAuthEnabled?: boolean;
  authenticated?: boolean;
  isLocalhost?: boolean;
}

const returnUrl = shallowRef("/");
const token = shallowRef("");
const errorMessage = shallowRef<string | null>(null);
const isSubmitting = shallowRef(false);
const authMode = shallowRef<"loading" | "token" | "oidc" | "unknown">("loading");

const signInHref = computed(() => apiUrl(`/auth/login?returnUrl=${encodeURIComponent(returnUrl.value)}`));
const buildLabel = computed(() => `v${import.meta.env.VITE_APP_VERSION} · ${import.meta.env.VITE_COMMIT_SHA}`);
const showTokenForm = computed(() => authMode.value !== "oidc");
const showOidcActions = computed(() => authMode.value !== "token");
const isLoadingOptions = computed(() => authMode.value === "loading" && !isSubmitting.value);
const referrerMetaElement = shallowRef<HTMLMetaElement | null>(null);

onMounted(() => {
  ensureNoReferrerMeta();
  returnUrl.value = resolveReturnUrl(window.location.search);
  const urlToken = new URLSearchParams(window.location.search).get("token");

  if (urlToken !== null) {
    token.value = urlToken;
  }

  void initialize(urlToken);
});

onUnmounted(() => {
  referrerMetaElement.value?.remove();
  referrerMetaElement.value = null;
});

async function initialize(urlToken: string | null): Promise<void> {
  await loadAuthMode();

  if (authMode.value !== "token" || urlToken === null) {
    return;
  }

  await submitToken(urlToken, true);
}

async function loadAuthMode(): Promise<void> {
  try {
    const response = await apiFetch("/api/auth/status");

    if (!response.ok) {
      throw new Error(`Auth status request failed with status ${response.status}.`);
    }

    const config = await response.json() as AuthStatusResponse;

    if (config.authEnabled) {
      authMode.value = "oidc";
      return;
    }

    // Localhost requests bypass token auth — redirect to backend auto-sign-in
    if (config.isLocalhost && !config.authEnabled) {
      window.location.replace(apiUrl(`/auth/login?returnUrl=${encodeURIComponent(returnUrl.value)}`));
      return;
    }

    if (config.tokenAuthEnabled) {
      authMode.value = "token";
      return;
    }

    window.location.replace("/");
  } catch {
    authMode.value = "unknown";
  }
}

async function onSubmit(): Promise<void> {
  await submitToken(token.value, false);
}

async function submitToken(value: string, isAutoSubmit: boolean): Promise<void> {
  const trimmedToken = value.trim();

  token.value = trimmedToken;
  errorMessage.value = null;

  if (trimmedToken.length === 0) {
    errorMessage.value = "Invalid token";
    return;
  }

  isSubmitting.value = true;

  try {
    const response = await apiFetch("/auth/token-login", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ token: trimmedToken }),
    });

    if (!response.ok) {
      throw new Error(`Token login failed with status ${response.status}.`);
    }

    if (isAutoSubmit) {
      scrubTokenFromUrl();
    }

    window.location.replace(returnUrl.value);
  } catch {
    errorMessage.value = "Invalid token";
  } finally {
    isSubmitting.value = false;
  }
}

function ensureNoReferrerMeta(): void {
  const existingMeta = document.head.querySelector('meta[name="referrer"]');

  if (existingMeta instanceof HTMLMetaElement) {
    existingMeta.content = "no-referrer";
    return;
  }

  const meta = document.createElement("meta");
  meta.name = "referrer";
  meta.content = "no-referrer";
  document.head.append(meta);
  referrerMetaElement.value = meta;
}

function scrubTokenFromUrl(): void {
  const currentUrl = new URL(window.location.href);
  currentUrl.searchParams.delete("token");
  const cleanUrl = `${currentUrl.pathname}${currentUrl.search}${currentUrl.hash}`;
  window.history.replaceState(null, "", cleanUrl);
}

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
          <img
            :src="weaveLogo"
            alt="Weave logo"
            class="logo-mark"
          >
        </div>

        <div>
          <h1 class="font-mono text-3xl font-bold tracking-tight text-text">
            Weave
          </h1>
          <p class="mt-1 font-mono text-sm text-muted">
            Agent Fleet
          </p>
        </div>

        <p class="text-sm leading-relaxed text-muted">
          Orchestrate AI coding agents across your projects.
        </p>

        <div class="flex w-full flex-col gap-3">
          <div
            v-if="isLoadingOptions"
            class="rounded-btn border border-border bg-main-bg px-4 py-3 text-sm text-muted"
            aria-live="polite"
            role="status"
          >
            Loading sign-in options…
          </div>

          <form
            v-else-if="showTokenForm"
            class="flex w-full flex-col gap-3 text-left"
            @submit.prevent="onSubmit"
          >
            <label
              class="text-sm font-medium text-text"
              for="login-token"
            >
              Access token
            </label>

            <input
              id="login-token"
              v-model="token"
              type="text"
              autocomplete="off"
              spellcheck="false"
              placeholder="Paste your token"
              class="w-full rounded-btn border border-border bg-main-bg px-3 py-2.5 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent/60"
              :disabled="isSubmitting"
            >

            <p
              v-if="errorMessage"
              class="text-sm text-destructive"
              role="alert"
            >
              {{ errorMessage }}
            </p>

            <button
              type="submit"
              class="inline-flex items-center justify-center rounded-btn bg-primary px-6 py-2.5 text-sm font-medium text-white transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60"
              :disabled="isSubmitting"
            >
              {{ isSubmitting ? "Signing in…" : "Continue" }}
            </button>
          </form>

          <template v-else>
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
          </template>

          <template v-if="showOidcActions && showTokenForm && authMode === 'unknown'">
            <a
              :href="signInHref"
              class="inline-flex items-center justify-center rounded-btn border border-border bg-main-bg px-6 py-2.5 text-sm font-medium text-text transition-colors hover:border-accent/50"
            >
              Use browser sign in instead
            </a>
          </template>
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
