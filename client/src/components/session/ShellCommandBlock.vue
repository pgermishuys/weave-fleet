<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { ChevronDown, ChevronUp, SquareTerminal } from "lucide-vue-next";
import type { ShellCommandView } from "@/lib/shell-commands";

/**
 * A shell command the user ran from the composer (`!git status`) and what it printed: one block, on the user's side
 * of the conversation, since the user ran it, not the agent. Long output starts collapsed to its last lines, the end
 * being where a failing test or a build error says what went wrong.
 */
const props = defineProps<{
  command: ShellCommandView;
}>();

/** Lines shown while collapsed. */
const COLLAPSED_LINES = 12;

const expanded = shallowRef(false);

const lines = computed(() => {
  const text = props.command.output.replace(/\n+$/, "");
  return text.length === 0 ? [] : text.split("\n");
});

const isLong = computed(() => lines.value.length > COLLAPSED_LINES);
const hiddenLines = computed(() => (isLong.value && !expanded.value ? lines.value.length - COLLAPSED_LINES : 0));
const shownOutput = computed(() => lines.value.slice(hiddenLines.value).join("\n"));

const stateLabel = computed(() => {
  switch (props.command.state) {
    case "running":
      return "Running…";
    case "failed":
      return props.command.exit !== undefined ? `Failed · exit ${props.command.exit}` : "Failed";
    case "timeout":
      return "Timed out";
    case "killed":
      return "Stopped";
    default:
      return props.command.exit !== undefined ? `exit ${props.command.exit}` : "";
  }
});
</script>

<template>
  <section
    class="shell-block"
    :class="`shell-block--${command.state}`"
    data-testid="shell-command"
    :data-state="command.state"
    aria-label="Shell command you ran"
  >
    <header class="shell-block__head">
      <SquareTerminal
        class="shell-block__icon"
        aria-hidden="true"
      />
      <span class="shell-block__who">You ran</span>
      <span
        v-if="stateLabel"
        class="shell-block__state"
        data-testid="shell-command-state"
      >{{ stateLabel }}</span>
    </header>
    <pre
      class="shell-block__command"
      data-testid="shell-command-text"
    ><span
      class="shell-block__prompt"
      aria-hidden="true"
    >$ </span>{{ command.command }}</pre>
    <button
      v-if="hiddenLines > 0"
      type="button"
      class="shell-block__toggle"
      data-testid="shell-command-expand"
      @click="expanded = true"
    >
      <ChevronUp
        class="shell-block__toggle-icon"
        aria-hidden="true"
      />
      Show {{ hiddenLines }} earlier {{ hiddenLines === 1 ? "line" : "lines" }}
    </button>
    <pre
      v-if="lines.length > 0"
      class="shell-block__output"
      data-testid="shell-command-output"
    >{{ shownOutput }}</pre>
    <p
      v-else-if="command.state !== 'running'"
      class="shell-block__note"
    >
      No output
    </p>
    <button
      v-if="isLong && expanded"
      type="button"
      class="shell-block__toggle"
      @click="expanded = false"
    >
      <ChevronDown
        class="shell-block__toggle-icon"
        aria-hidden="true"
      />
      Show only the last {{ COLLAPSED_LINES }} lines
    </button>
    <p
      v-if="command.truncated"
      class="shell-block__note"
    >
      The harness kept only part of the output.
    </p>
  </section>
</template>

<style scoped>
.shell-block {
  align-self: flex-end;
  width: 82%;
  max-width: 100%;
  box-sizing: border-box;
  padding: 8px 10px 10px;
  border: 1px solid var(--border);
  border-radius: calc(var(--radius-panel) + 2px) calc(var(--radius-panel) + 2px) 4px calc(var(--radius-panel) + 2px);
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.shell-block--failed,
.shell-block--timeout,
.shell-block--killed {
  border-color: color-mix(in srgb, var(--error) 40%, var(--border));
}

.shell-block__head {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 6px;
  color: var(--muted);
  font-size: 12px;
}

.shell-block__icon {
  width: 13px;
  height: 13px;
  flex-shrink: 0;
}

.shell-block__who {
  font-weight: 500;
  color: var(--text);
}

.shell-block__state {
  margin-left: auto;
  font-variant-numeric: tabular-nums;
}

.shell-block--failed .shell-block__state,
.shell-block--timeout .shell-block__state,
.shell-block--killed .shell-block__state {
  color: var(--error);
}

.shell-block__command,
.shell-block__output {
  margin: 0;
  font-family: var(--font-mono-stack);
  font-size: 12px;
  line-height: 1.55;
  white-space: pre-wrap;
  word-break: break-word;
}

.shell-block__command {
  color: var(--text);
}

.shell-block__prompt {
  color: var(--muted);
  user-select: none;
}

.shell-block__output {
  max-height: 480px;
  margin-top: 6px;
  padding: 8px 10px;
  overflow: auto;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: color-mix(in srgb, var(--main-bg) 60%, transparent);
  color: var(--muted);
}

.shell-block__toggle {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin-top: 6px;
  padding: 0;
  border: none;
  background: transparent;
  color: var(--muted);
  font-size: 12px;
  cursor: pointer;
}

.shell-block__toggle:hover {
  color: var(--text);
}

.shell-block__toggle:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.shell-block__toggle-icon {
  width: 12px;
  height: 12px;
}

.shell-block__note {
  margin: 6px 0 0;
  color: var(--muted);
  font-size: 12px;
}

@media (max-width: 640px) {
  .shell-block {
    width: 100%;
  }
}
</style>
