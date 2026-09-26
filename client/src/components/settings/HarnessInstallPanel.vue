<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { Check, Copy, Download, ExternalLink } from "lucide-vue-next";
import type { HarnessInfo } from "@/api/client";
import { harnessState } from "@/lib/harness-display";
import { useHarnessSetupStore } from "@/stores/harness-setup";

/**
 * How a harness is installed here, under its card in Settings → Harnesses: the install's mode, the version, the
 * folders it uses, what to know about it, and its provider sign-in. Shown for harnesses that describe their
 * install (`setup.mode`), such as OpenCode 2, which can go in a folder of its own next to OpenCode 1. Until it's
 * installed, it shows where it can go and the command for each place, to run here (Set up) or in any terminal.
 */

const props = defineProps<{
  harness: HarnessInfo;
  /** Fleet signs in to the harness's providers itself (the panel below it); the terminal is then the fallback. */
  signsInHere?: boolean;
}>();

const harnessSetup = useHarnessSetupStore();

const setup = computed(() => props.harness.setup ?? null);
const installed = computed(() => Boolean(props.harness.executablePath));
/** Not installed, or what's there isn't this harness any more: it's installed (again) with the installer. */
const needsInstall = computed(() => {
  const state = harnessState(props.harness);
  return state === "not-installed" || state === "not-working";
});
const choices = computed(() => (needsInstall.value ? setup.value?.installChoices ?? [] : []));
const hasChoices = computed(() => choices.value.length > 1);
const folders = computed(() => setup.value?.folders ?? []);
const notes = computed(() => setup.value?.notes ?? []);
/** The one installer to show when there's no place to pick. */
const installCommand = computed(() => (needsInstall.value && !hasChoices.value ? setup.value?.installCommand ?? null : null));
/** Signing in only means something once it's installed. */
const signIn = computed(() => (installed.value && !needsInstall.value ? setup.value?.signInCommand ?? null : null));

/** The command copied last, while its button says so. */
const copied = shallowRef<string | null>(null);

async function copy(command: string): Promise<void> {
  try {
    await navigator.clipboard?.writeText(command);
    copied.value = command;
    setTimeout(() => {
      if (copied.value === command) copied.value = null;
    }, 1500);
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
        v-if="!hasChoices"
        class="harness-install__mode"
        data-testid="harness-install-mode"
      >{{ setup.mode }}</span>
      <span
        v-if="needsInstall"
        class="harness-install__version"
      >{{ installed ? "Needs installing again" : "Not installed yet" }}</span>
      <span
        v-else-if="harness.version"
        class="harness-install__version"
      >{{ harness.version }}</span>
    </div>

    <div
      v-if="hasChoices"
      class="harness-install__choices"
      data-testid="harness-install-choices"
    >
      <p class="harness-install__lead">
        It can go in one of two places. Pick one when you install it:
      </p>
      <section
        v-for="choice in choices"
        :key="choice.id"
        class="harness-install__choice"
        :data-testid="`harness-install-choice-${choice.id}`"
      >
        <p class="harness-install__choice-label">
          {{ choice.label }}
          <span
            v-if="choice.recommended"
            class="harness-install__mode"
          >Recommended</span>
        </p>
        <p class="harness-install__choice-description">
          {{ choice.description }}
        </p>
        <dl class="harness-install__folders">
          <template
            v-for="folder in choice.folders"
            :key="folder.label"
          >
            <dt>{{ folder.label }}</dt>
            <dd>{{ folder.path }}</dd>
          </template>
        </dl>
        <div class="harness-install__copy">
          <code>{{ choice.command }}</code>
          <button
            type="button"
            class="harness-install__btn"
            @click="void copy(choice.command)"
          >
            <Check
              v-if="copied === choice.command"
              :size="14"
              aria-hidden="true"
            />
            <Copy
              v-else
              :size="14"
              aria-hidden="true"
            />
            {{ copied === choice.command ? "Copied" : "Copy" }}
          </button>
        </div>
      </section>
    </div>

    <dl
      v-if="!hasChoices && folders.length > 0"
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
      v-if="installCommand"
      class="harness-install__sign-in"
    >
      <p>Install it in a terminal:</p>
      <div class="harness-install__copy">
        <code data-testid="harness-install-command">{{ installCommand }}</code>
        <button
          type="button"
          class="harness-install__btn"
          @click="void copy(installCommand)"
        >
          <Check
            v-if="copied === installCommand"
            :size="14"
            aria-hidden="true"
          />
          <Copy
            v-else
            :size="14"
            aria-hidden="true"
          />
          {{ copied === installCommand ? "Copied" : "Copy" }}
        </button>
      </div>
    </div>

    <button
      v-if="needsInstall && (installCommand || hasChoices)"
      type="button"
      class="harness-install__btn harness-install__set-up"
      data-testid="harness-install-set-up"
      @click="harnessSetup.open('harnesses')"
    >
      <Download
        :size="14"
        aria-hidden="true"
      />
      Install {{ harness.displayName }} here
    </button>

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
          @click="void copy(signIn)"
        >
          <Check
            v-if="copied === signIn"
            :size="14"
            aria-hidden="true"
          />
          <Copy
            v-else
            :size="14"
            aria-hidden="true"
          />
          {{ copied === signIn ? "Copied" : "Copy" }}
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

.harness-install__lead {
  color: var(--muted);
}

.harness-install__choices {
  display: grid;
  gap: 10px;
}

.harness-install__choice {
  display: grid;
  gap: 8px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  padding: 10px 12px;
  background: var(--card-bg);
}

.harness-install__choice-label {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  color: var(--text);
  font-size: 13px;
  font-weight: 600;
}

.harness-install__choice-description {
  color: var(--muted);
}

.harness-install__set-up {
  justify-self: start;
}

.harness-install__folders {
  display: grid;
  grid-template-columns: fit-content(15em) minmax(0, 1fr);
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
