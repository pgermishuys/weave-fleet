<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { Button } from "@/components/ui/button";
import type { PermissionAsk } from "@/composables/use-session-permissions";
import type { PermissionReply } from "@/lib/push/answer";

/**
 * The answers to an agent's permission ask, as 44px rows: Allow once; Don't ask again for <pattern> this session;
 * Deny, which opens a field to tell the agent what to do instead. The same choices as the desktop PermissionCard.
 */
const props = defineProps<{ ask: PermissionAsk; busy?: boolean; compact?: boolean }>();
const emit = defineEmits<{ (event: "answer", reply: PermissionReply, message?: string): void }>();

const denying = shallowRef(false);
const instead = shallowRef("");

const lead = computed(() => {
  switch (props.ask.kind) {
    case "shell": return "Run a command";
    case "edit": return "Edit a file";
    case "read": return "Read a file";
    case "web": return "Open a web page";
    default: return `Use ${props.ask.tool}`;
  }
});
const always = computed(() => props.ask.always[0] ?? props.ask.tool);

function deny(): void {
  emit("answer", "reject", instead.value.trim() || undefined);
}
</script>

<template>
  <div
    class="pc"
    data-testid="permission-choices"
  >
    <div class="pc__lead">
      <span
        class="pc__dot"
        aria-hidden="true"
      />{{ lead }}
      <span
        v-if="ask.subagent"
        class="pc__sub"
      >· {{ ask.subagent }}</span>
    </div>
    <pre
      v-if="ask.title"
      class="pc__cmd"
    ><span
      v-if="ask.directory"
      class="pc__dim"
    >{{ ask.directory }} $ </span>{{ ask.title }}</pre>
    <pre
      v-if="ask.detail && !compact"
      class="pc__cmd pc__detail"
    >{{ ask.detail }}</pre>

    <button
      type="button"
      class="pc__choice"
      :disabled="busy"
      data-testid="permission-once"
      @click="emit('answer', 'once')"
    >
      <span class="pc__key">1</span>Allow once
    </button>
    <button
      type="button"
      class="pc__choice"
      :disabled="busy"
      data-testid="permission-always"
      @click="emit('answer', 'always')"
    >
      <span class="pc__key">2</span>
      <span class="pc__label">Don't ask again for <code>{{ always }}</code></span>
      <span class="pc__scope">this session</span>
    </button>
    <button
      v-if="!denying"
      type="button"
      class="pc__choice"
      :disabled="busy"
      data-testid="permission-deny"
      @click="denying = true"
    >
      <span class="pc__key">3</span>Deny, and tell the agent what to do instead…
    </button>
    <form
      v-else
      class="pc__deny"
      @submit.prevent="deny"
    >
      <textarea
        v-model="instead"
        class="pc__input"
        rows="2"
        placeholder="Tell the agent what to do instead (optional)"
        aria-label="What the agent should do instead"
        data-testid="permission-deny-text"
      />
      <div class="pc__deny-actions">
        <Button
          type="button"
          variant="ghost"
          size="sm"
          @click="denying = false"
        >
          Back
        </Button>
        <Button
          type="submit"
          size="sm"
          variant="destructive"
          :disabled="busy"
          data-testid="permission-deny-send"
        >
          Deny
        </Button>
      </div>
    </form>
  </div>
</template>

<style scoped>
.pc {
  display: grid;
  gap: 8px;
  padding: 12px;
  border: 1px solid color-mix(in srgb, var(--idle) 45%, transparent);
  border-radius: var(--radius-card);
  background: var(--card-bg);
}

.pc__lead {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  font-weight: 600;
}

.pc__dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--idle);
}

.pc__sub {
  font-weight: 400;
  color: var(--muted);
}

.pc__cmd {
  margin: 0;
  padding: 8px 10px;
  max-height: 160px;
  overflow: auto;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--main-bg);
  font-family: var(--font-mono-stack);
  font-size: 12px;
  line-height: 1.45;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.pc__detail {
  max-height: 120px;
}

.pc__dim {
  color: var(--muted);
}

.pc__choice {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 44px;
  padding: 8px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  font: inherit;
  font-size: 14px;
  text-align: left;
  cursor: pointer;
}

.pc__choice:active {
  background: var(--accent-dim);
}

.pc__choice:disabled {
  opacity: 0.6;
}

.pc__key {
  display: grid;
  width: 20px;
  height: 20px;
  flex: none;
  place-items: center;
  border: 1px solid var(--border);
  border-radius: 6px;
  font-size: 11px;
  color: var(--muted);
}

.pc__label {
  flex: 1;
  min-width: 0;
}

.pc__label code {
  font-family: var(--font-mono-stack);
  font-size: 12px;
}

.pc__scope {
  font-size: 11px;
  color: var(--muted);
}

.pc__deny {
  display: grid;
  gap: 8px;
}

.pc__input {
  width: 100%;
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--main-bg);
  color: var(--text);
  font: inherit;
  font-size: 15px;
  resize: vertical;
}

.pc__deny-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
