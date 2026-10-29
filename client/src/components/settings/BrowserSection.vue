<script setup lang="ts">
import { onMounted } from "vue";
import { Info, LoaderCircle } from "lucide-vue-next";
import { useAgentBrowserSettings, type BrowserPages as Pages } from "@/composables/use-agent-browser-settings";

/**
 * Settings → Permissions → Browser: whether agents can use a browser of their own, which pages it may open, and whether
 * page scripts run. Fleet runs every browser action, so these limits hold whatever an agent writes. How each harness gets
 * the browser is on that harness's tab, under What it may do.
 */

const PAGES: readonly { id: Pages; label: string; description: string }[] = [
  { id: "session", label: "This session's pages", description: "Apps it started and pages in its canvases, on this machine. Recommended." },
  { id: "machine", label: "Any page on this machine", description: "Any localhost address, such as a server you started yourself." },
  { id: "any", label: "Any address", description: "Includes the internet. Pages can then send what they read anywhere." },
];

const { settings, loading, saving, error, load, save } = useAgentBrowserSettings();

onMounted(() => void load());
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
          <small>Adds browser tools to sessions that can have them (each harness's tab says which). Off: OpenCode 2 doesn't see its browser tools, Claude Code and GitHub Copilot don't see Fleet's from their next message, and OpenCode's answer that the browser is off.</small>
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
</style>
