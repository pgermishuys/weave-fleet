<script setup lang="ts">
import { computed, nextTick, shallowRef, useTemplateRef } from "vue";
import { ArrowUp } from "lucide-vue-next";
import CommandText from "@/components/phone/CommandText.vue";
import type { PermissionAsk } from "@/composables/use-session-permissions";
import { dontAskAgain } from "@/lib/phone/asks";
import { autogrow } from "@/lib/phone/keyboard";
import type { PermissionReply } from "@/lib/push/answer";

/**
 * Every answer to an agent's permission ask, as the More… sheet shows it, numbered as the desktop PermissionCard
 * numbers them: where and what (the command wrapped only between words), 1 Allow once, 2 Don't ask again (what that
 * covers, for this session), and 3 Deny, which opens a box to tell the agent what to do instead.
 */
const props = defineProps<{ ask: PermissionAsk; busy?: boolean }>();
const emit = defineEmits<{ (event: "answer", reply: PermissionReply, message?: string): void; (event: "expand"): void }>();

const denying = shallowRef(false);
const instead = shallowRef("");
const denyBox = useTemplateRef<HTMLElement>("denyBox");
const always = computed(() => dontAskAgain(props.ask));
const diff = computed(() => props.ask.kind === "edit" && props.ask.detail
  ? props.ask.detail.split("\n").filter((line) => !/^(Index: |={3,}|-{3} |\+{3} )/.test(line))
  : null);

async function openDeny(): Promise<void> {
  denying.value = true;
  emit("expand");
  await nextTick();
  denyBox.value?.querySelector("textarea")?.focus({ preventScroll: true });
  setTimeout(() => denyBox.value?.scrollIntoView({ block: "nearest", behavior: "smooth" }), 350);
}

function deny(): void {
  emit("answer", "reject", instead.value.trim() || undefined);
}
</script>

<template>
  <div
    class="ph-sheet__pad"
    data-testid="permission-choices"
  >
    <p
      v-if="ask.subagent"
      class="pc__from"
    >
      Asked by <span class="ph-from">{{ ask.subagent }}</span>
    </p>
    <CommandText
      v-if="ask.title"
      :command="ask.title"
      :directory="ask.directory"
      :prompt="ask.kind === 'shell'"
      class="pc__cmd"
    />
    <pre
      v-if="diff"
      class="ph-cmd ph-cmd--pre"
    ><span
      v-for="(line, index) in diff"
      :key="index"
      :class="line.startsWith('+') ? 'ph-add' : line.startsWith('-') ? 'ph-del' : line.startsWith('@@') ? 'ph-cmd__p' : ''"
    >{{ line }}
</span></pre>
    <pre
      v-else-if="ask.detail"
      class="ph-cmd ph-cmd--pre"
    >{{ ask.detail }}</pre>
    <div
      class="ph-choices pc__choices"
      role="group"
      aria-label="Answer"
    >
      <button
        type="button"
        class="ph-choice ph-choice--primary"
        :disabled="busy"
        data-testid="permission-once"
        @click="emit('answer', 'once')"
      >
        <span
          class="ph-choice__n"
          aria-hidden="true"
        >1</span>
        <span class="ph-choice__main"><span class="ph-choice__t">Allow once</span></span>
      </button>
      <button
        type="button"
        class="ph-choice"
        :disabled="busy"
        data-testid="permission-always"
        @click="emit('answer', 'always')"
      >
        <span
          class="ph-choice__n"
          aria-hidden="true"
        >2</span>
        <span class="ph-choice__main"><span class="ph-choice__t">{{ always.lead }}<template v-if="always.code">
          {{ " " }}<code class="ph-inline-code">{{ always.code }}</code>
        </template></span></span>
        <span class="ph-choice__note">this session</span>
      </button>
      <button
        type="button"
        class="ph-choice"
        :class="{ 'ph-choice--selected': denying }"
        :disabled="busy"
        data-testid="permission-deny"
        @click="openDeny"
      >
        <span
          class="ph-choice__n"
          aria-hidden="true"
        >3</span>
        <span class="ph-choice__main"><span class="ph-choice__t">Deny, and tell the agent what to do instead…</span></span>
      </button>
    </div>
    <div
      v-if="denying"
      ref="denyBox"
      class="pc__deny"
    >
      <form
        class="ph-frame"
        @submit.prevent="deny"
      >
        <textarea
          v-model="instead"
          class="phone-composer-input"
          rows="2"
          placeholder="What to do instead (optional)"
          aria-label="What the agent should do instead"
          data-testid="permission-deny-text"
          @input="autogrow($event.target as HTMLTextAreaElement)"
        />
        <div class="ph-frame__bar">
          <span class="ph-sel ph-sel--note">Tells the agent, then denies</span>
          <button
            type="submit"
            class="ph-send"
            :disabled="busy"
            aria-label="Deny"
            data-testid="permission-deny-send"
          >
            <ArrowUp aria-hidden="true" />
          </button>
        </div>
      </form>
    </div>
    <p class="ph-choices-hint">
      The agent waits until you answer.
    </p>
  </div>
</template>

<style scoped>
.pc__from {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0 0 10px;
  font-size: var(--ph-t-meta);
  color: var(--muted);
}

.pc__cmd {
  margin-top: 0;
}

.pc__choices {
  margin-top: 12px;
}

.pc__deny {
  margin-top: 10px;
}

.pc__deny textarea {
  height: auto;
  min-height: 62px;
}
</style>
