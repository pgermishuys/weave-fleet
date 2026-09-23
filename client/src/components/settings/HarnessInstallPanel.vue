<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { Check, Copy, ExternalLink } from "lucide-vue-next";
import type { HarnessInfo } from "@/api/client";

/**
 * How a harness is installed here, under its card in Settings → Harnesses: the install's mode, the version, the
 * folders it uses, what to know about it, and its provider sign-in. Shown for harnesses that describe their
 * install (`setup.mode`), such as OpenCode 2, which installs separately next to OpenCode 1.
 */

const props = defineProps<{
  harness: HarnessInfo;
  /** Fleet signs in to the harness's providers itself (the panel below it); the terminal is then the fallback. */
  signsInHere?: boolean;
}>();

const setup = computed(() => props.harness.setup ?? null);
const installed = computed(() => Boolean(props.harness.executablePath));
const folders = computed(() => setup.value?.folders ?? []);
const notes = computed(() => setup.value?.notes ?? []);
/** Signing in only means something once it's installed. */
const signIn = computed(() => (installed.value ? setup.value?.signInCommand ?? null : null));

const copied = shallowRef(false);

async function copySignIn(): Promise<void> {
  if (!signIn.value) return;
  try {
    await navigator.clipboard?.writeText(signIn.value);
    copied.value = true;
    setTimeout(() => (copied.value = false), 1500);
  } catch {
    // The command is on screen to select by hand.
  }
}
</script>

<template>
  <div
    v-if="setup?.mode"
    class="harness-install"
    data-testid="harness-install"
  >
    <div class="harness-install__head">
      <p class="harness-install__title">
        Install
      </p>
      <span
        class="harness-install__mode"
        data-testid="harness-install-mode"
      >{{ setup.mode }}</span>
      <span
        v-if="harness.version"
        class="harness-install__version"
      >{{ harness.version }}</span>
      <span
        v-else-if="!installed"
        class="harness-install__version"
      >Not installed yet</span>
    </div>

    <dl
      v-if="folders.length > 0"
      class="harness-install__folders"
    >
      <template
        v-for="folder in folders"
        :key="folder.label"
      >
        <dt>{{ folder.label }}</dt>
        <dd>{{ folder.path }}</dd>
      </template>
    </dl>

    <ul
      v-if="notes.length > 0"
      class="harness-install__notes"
    >
      <li
        v-for="note in notes"
        :key="note"
      >
        {{ note }}
      </li>
    </ul>

    <div
      v-if="signIn"
      class="harness-install__sign-in"
    >
      <p>{{ signsInHere ? "Or sign in to a provider in a terminal:" : "Sign in to a provider in a terminal:" }}</p>
      <div class="harness-install__copy">
        <code data-testid="harness-install-sign-in">{{ signIn }}</code>
        <button
          type="button"
          class="harness-install__btn"
          @click="void copySignIn()"
        >
          <Check
            v-if="copied"
            :size="14"
            aria-hidden="true"
          />
          <Copy
            v-else
            :size="14"
            aria-hidden="true"
          />
          {{ copied ? "Copied" : "Copy" }}
        </button>
      </div>
    </div>

    <a
      v-if="setup.downloadUrl && !installed"
      :href="setup.downloadUrl"
      target="_blank"
      rel="noopener noreferrer"
      class="harness-install__link"
    >
      Download {{ harness.displayName }}
      <ExternalLink
        :size="12"
        aria-hidden="true"
      />
    </a>
  </div>
</template>

<style scoped>
.harness-install {
  display: grid;
  gap: 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  padding: 14px 16px;
  background: var(--main-bg);
  font-size: 12.5px;
}

.harness-install__head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

.harness-install__title {
  color: var(--text);
  font-size: 13px;
  font-weight: 600;
}

.harness-install__mode {
  border: 1px solid color-mix(in srgb, var(--accent) 30%, transparent);
  border-radius: 999px;
  padding: 1px 8px;
  background: color-mix(in srgb, var(--accent) 10%, transparent);
  color: var(--accent);
  font-size: 11px;
  font-weight: 500;
}

.harness-install__version {
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
}

.harness-install__folders {
  display: grid;
  grid-template-columns: max-content minmax(0, 1fr);
  gap: 4px 14px;
  margin: 0;
}

.harness-install__folders dt {
  color: var(--muted);
}

.harness-install__folders dd {
  margin: 0;
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  overflow-wrap: anywhere;
}

.harness-install__notes {
  display: grid;
  gap: 4px;
  margin: 0;
  padding-left: 18px;
  color: var(--muted);
  list-style: disc;
}

.harness-install__sign-in {
  display: grid;
  gap: 6px;
  color: var(--muted);
}

.harness-install__copy {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

.harness-install__copy code {
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 5px 8px;
  background: var(--card-bg);
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  overflow-wrap: anywhere;
}

.harness-install__btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  padding: 5px 10px;
  background: var(--card-bg);
  color: var(--text);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: border-color var(--transition);
}

.harness-install__btn:hover {
  border-color: color-mix(in srgb, var(--accent) 50%, transparent);
}

.harness-install__link {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  justify-self: start;
  color: var(--accent);
  font-weight: 500;
}
</style>
