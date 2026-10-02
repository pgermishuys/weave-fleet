<script setup lang="ts">
import { computed, onMounted, shallowRef } from "vue";
import { Info, LoaderCircle } from "lucide-vue-next";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import { apiFetch } from "@/lib/api-client";

/**
 * Settings → Browser: whether agents can use a browser of their own, which pages it may open, and whether page scripts
 * run. Fleet runs every browser action, so these limits hold whatever an agent writes. The rows below say how each
 * harness gets the browser, or why it doesn't.
 */

type Pages = "session" | "machine" | "any";

interface BrowserSettings {
  enabled: boolean;
  pages: Pages;
  scripts: boolean;
}

const PAGES: readonly { id: Pages; label: string; description: string }[] = [
  { id: "session", label: "This session's pages", description: "Apps it started and pages in its canvases, on this machine. Recommended." },
  { id: "machine", label: "Any page on this machine", description: "Any localhost address, such as a server you started yourself." },
  { id: "any", label: "Any address", description: "Includes the internet. Pages can then send what they read anywhere." },
];

/** How each harness gets the browser; the ones without it are off with a reason. */
const HOW: Record<string, string> = {
  opencode2: "Its own browser tools, in Code Mode. Fleet is the browser behind them.",
  opencode: "Fleet's browser tools: read the page, act on it.",
};

const settings = shallowRef<BrowserSettings>({ enabled: true, pages: "session", scripts: false });
const loading = shallowRef(true);
const saving = shallowRef(false);
const error = shallowRef<string | null>(null);
const { harnesses } = useEnabledHarnesses();

const rows = computed(() =>
  harnesses.value.map((harness) => {
    const supported = harness.capabilities?.supportsAgentBrowser === true;
    return {
      type: harness.type,
      name: harness.displayName ?? harness.type,
      on: supported && settings.value.enabled,
      how: supported ? HOW[harness.type] ?? "Fleet's browser tools." : `Off: Fleet can't add tools to ${harness.displayName ?? harness.type} yet.`,
    };
  }),
);

onMounted(async () => {
  try {
    const response = await apiFetch("/api/agent-browser/settings");
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    settings.value = (await response.json()) as BrowserSettings;
  } catch {
    error.value = "Fleet couldn't load the browser settings. Try again in a moment.";
  } finally {
    loading.value = false;
  }
});

async function save(change: Partial<BrowserSettings>): Promise<void> {
  const before = settings.value;
  settings.value = { ...before, ...change };
  saving.value = true;
  error.value = null;
  try {
    const response = await apiFetch("/api/agent-browser/settings", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(change),
    });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    settings.value = (await response.json()) as BrowserSettings;
  } catch {
    settings.value = before;
    error.value = "Fleet couldn't save that. Try again.";
  } finally {
    saving.value = false;
  }
}
</script>

<template>
  <section
    class="rounded-card border border-border bg-card-bg p-6 shadow-sm"
    data-testid="browser-section"
  >
    <div class="flex items-start gap-4">
      <div class="flex min-w-0 flex-1 flex-col gap-1">
        <h2 class="text-lg font-semibold text-text">
          Browser
        </h2>
        <p class="text-sm text-muted">
          Agents can open their own tab on a page, read it, click and type, to try what they built. They use Fleet's
          headless browser, not yours, so your view and your sign-ins don't change.
        </p>
      </div>
      <LoaderCircle
        v-if="loading || saving"
        :size="16"
        class="mt-1 animate-spin text-muted"
        aria-hidden="true"
      />
    </div>

    <p
      v-if="error"
      class="mt-3 text-sm text-error"
      role="alert"
    >
      {{ error }}
    </p>

    <div class="browser-settings mt-5">
      <div class="browser-row">
        <div class="browser-row__text">
          <b>Agents can use a browser</b>
          <small>Adds browser tools to sessions that can have them (below). Off: OpenCode 2 doesn't see its browser tools, and OpenCode's answer that the browser is off.</small>
        </div>
        <button
          type="button"
          role="switch"
          :aria-checked="settings.enabled"
          :disabled="loading || saving"
          aria-label="Agents can use a browser"
          data-testid="browser-enabled-switch"
          class="browser-switch"
          @click="save({ enabled: !settings.enabled })"
        />
      </div>

      <div
        class="browser-row browser-row--stack"
        :class="{ 'browser-row--off': !settings.enabled }"
      >
        <div class="browser-row__text">
          <b>Pages agents can open</b>
          <small>Fleet checks every address before the agent's tab goes there, links it clicks included.</small>
        </div>
        <div
          class="browser-radios"
          role="radiogroup"
          aria-label="Pages agents can open"
        >
          <label
            v-for="option in PAGES"
            :key="option.id"
            class="browser-radio"
          >
            <input
              type="radio"
              name="browser-pages"
              :value="option.id"
              :checked="settings.pages === option.id"
              :disabled="loading || saving || !settings.enabled"
              :data-testid="`browser-pages-${option.id}`"
              @change="save({ pages: option.id })"
            >
            <span>
              {{ option.label }}
              <small>{{ option.description }}</small>
            </span>
          </label>
        </div>
      </div>

      <div
        class="browser-row"
        :class="{ 'browser-row--off': !settings.enabled }"
      >
        <div class="browser-row__text">
          <b>Run scripts in pages</b>
          <small>Lets the agent run its own JavaScript in a page. Useful for checks, but a script can read the page's storage and call other local services.</small>
        </div>
        <button
          type="button"
          role="switch"
          :aria-checked="settings.scripts"
          :disabled="loading || saving || !settings.enabled"
          aria-label="Run scripts in pages"
          data-testid="browser-scripts-switch"
          class="browser-switch"
          @click="save({ scripts: !settings.scripts })"
        />
      </div>

      <div class="browser-row browser-row--off">
        <div class="browser-row__text">
          <b>Upload files to pages</b>
          <small>Not in this version. OpenCode 2 reads the file itself and Fleet only sees its name, so Fleet can't keep uploads inside the session's folder.</small>
        </div>
        <button
          type="button"
          role="switch"
          aria-checked="false"
          disabled
          aria-label="Upload files to pages"
          class="browser-switch"
        />
      </div>

      <div class="browser-row browser-row--off">
        <div class="browser-row__text">
          <b>Performance traces and memory snapshots</b>
          <small>Later. OpenCode 2 offers them; Fleet answers "not supported" until they're built.</small>
        </div>
        <span class="browser-later">Later</span>
      </div>
    </div>

    <div class="mt-5 grid gap-2">
      <h3 class="text-sm font-semibold text-text">
        Each harness
      </h3>
      <div class="browser-harnesses">
        <div
          v-for="row in rows"
          :key="row.type"
          class="browser-harness"
          :data-testid="`browser-harness-${row.type}`"
        >
          <span class="browser-harness__name">{{ row.name }}</span>
          <span class="browser-harness__how">{{ row.how }}</span>
          <span
            class="browser-harness__state"
            :class="row.on ? 'browser-harness__state--on' : ''"
          >{{ row.on ? "On" : "Off" }}</span>
        </div>
      </div>
    </div>

    <p class="browser-fine mt-5">
      <Info
        :size="13"
        aria-hidden="true"
      />
      <span>
        The agent's tabs share the browser Fleet uses for screenshots: about 240 MB while a tab is open. They close after
        15 minutes without use, and the browser quits two minutes after its last use.
      </span>
    </p>
  </section>
</template>

<style scoped>
.browser-settings {
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--main-bg);
}

.browser-row {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 13px 16px;
  border-bottom: 1px solid var(--border);
}

.browser-row:last-child {
  border-bottom: 0;
}

.browser-row--stack {
  flex-direction: column;
  align-items: stretch;
  gap: 8px;
}

.browser-row--off .browser-row__text,
.browser-row--off .browser-radios {
  opacity: 0.6;
}

.browser-row__text {
  display: grid;
  gap: 2px;
  flex: 1;
  min-width: 0;
}

.browser-row__text b {
  font-size: 13px;
  font-weight: 500;
  color: var(--text);
}

.browser-row__text small,
.browser-radio small {
  font-size: 12px;
  color: var(--muted);
}

.browser-radios {
  display: grid;
  gap: 6px;
}

.browser-radio {
  display: flex;
  align-items: flex-start;
  gap: 9px;
  font-size: 12.5px;
  color: var(--text);
  cursor: pointer;
}

.browser-radio span {
  display: grid;
}

.browser-radio input {
  margin-top: 2px;
  accent-color: var(--accent);
}

.browser-switch {
  position: relative;
  flex-shrink: 0;
  width: 36px;
  height: 20px;
  border-radius: 999px;
  background: var(--border);
  cursor: pointer;
  transition: background var(--transition);
}

.browser-switch::after {
  content: "";
  position: absolute;
  top: 2px;
  left: 2px;
  width: 16px;
  height: 16px;
  border-radius: 50%;
  background: #fff;
  box-shadow: 0 1px 2px rgb(0 0 0 / 0.25);
  transition: transform var(--transition);
}

.browser-switch[aria-checked="true"] {
  background: var(--accent);
}

.browser-switch[aria-checked="true"]::after {
  transform: translateX(16px);
}

.browser-switch:disabled {
  cursor: not-allowed;
  opacity: 0.6;
}

.browser-switch:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.browser-later {
  flex-shrink: 0;
  padding: 2px 8px;
  border-radius: 999px;
  font-size: 11px;
  color: var(--muted);
  background: color-mix(in srgb, var(--text) 7%, transparent);
}

.browser-harnesses {
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--main-bg);
}

.browser-harness {
  display: grid;
  grid-template-columns: 130px minmax(0, 1fr) auto;
  gap: 4px 12px;
  align-items: center;
  padding: 10px 16px;
  border-bottom: 1px solid var(--border);
  font-size: 12.5px;
}

.browser-harness:last-child {
  border-bottom: 0;
}

.browser-harness__name {
  font-weight: 600;
  color: var(--text);
}

.browser-harness__how {
  color: var(--muted);
}

.browser-harness__state {
  padding: 1px 7px;
  border-radius: 4px;
  font-size: 10.5px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: var(--muted);
  background: color-mix(in srgb, var(--text) 7%, transparent);
}

.browser-harness__state--on {
  color: var(--running);
  background: color-mix(in srgb, var(--running) 15%, transparent);
}

.browser-fine {
  display: flex;
  gap: 8px;
  align-items: flex-start;
  font-size: 12px;
  color: var(--muted);
}

.browser-fine :deep(svg) {
  flex-shrink: 0;
  margin-top: 2px;
}

@media (max-width: 640px) {
  .browser-harness {
    grid-template-columns: minmax(0, 1fr) auto;
  }

  .browser-harness__how {
    grid-column: 1 / -1;
    grid-row: 2;
  }
}
</style>
