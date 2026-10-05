<script setup lang="ts">
import { computed, nextTick, shallowRef, useTemplateRef } from "vue";
import { ArrowUp, ChevronRight, Monitor } from "lucide-vue-next";
import CommandText from "@/components/phone/CommandText.vue";
import type { PermissionAsk } from "@/composables/use-session-permissions";
import { alwaysCovers } from "@/lib/phone/asks";
import type { PermissionReply } from "@/lib/push/answer";

/**
 * Every answer to an agent's permission ask, as the More… sheet shows it: where and what (the command wrapped only
 * between words), Allow once, Always allow in this session (what that covers), and Deny, which opens a box to tell
 * the agent what to do instead. The same choices as the desktop PermissionCard.
 */
const props = defineProps<{ ask: PermissionAsk; busy?: boolean; machineName?: string; sessionTitle?: string }>();
const emit = defineEmits<{ (event: "answer", reply: PermissionReply, message?: string): void; (event: "expand"): void }>();

const denying = shallowRef(false);
const instead = shallowRef("");
const denyBox = useTemplateRef<HTMLElement>("denyBox");
const covers = computed(() => alwaysCovers(props.ask));

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
  <div data-testid="permission-choices">
    <div class="ph-sheet__pad">
      <div
        v-if="machineName || sessionTitle || ask.subagent"
        class="ph-ask__meta"
      >
        <span
          v-if="machineName"
          class="ph-machine"
        ><Monitor
          :size="14"
          aria-hidden="true"
        />{{ machineName }}</span>
        <span>{{ [sessionTitle, ask.subagent ? `asked by ${ask.subagent}` : ""].filter(Boolean).map((part) => `· ${part}`).join(" ") }}</span>
      </div>
      <CommandText
        v-if="ask.title"
        :command="ask.title"
        :directory="ask.directory"
        class="pc__cmd"
      />
      <pre
        v-if="ask.detail"
        class="ph-code pc__detail"
      >{{ ask.detail }}</pre>
      <button
        type="button"
        class="ph-btn ph-btn--primary ph-btn--big"
        :disabled="busy"
        data-testid="permission-once"
        @click="emit('answer', 'once')"
      >
        Allow once
      </button>
    </div>
    <div class="ph-group pc__group">
      <button
        type="button"
        class="ph-row"
        :disabled="busy"
        data-testid="permission-always"
        @click="emit('answer', 'always')"
      >
        <span class="ph-row__main">
          <span class="ph-row__title">Always allow in this session</span>
          <span class="ph-row__sub ph-row__sub--wrap">{{ covers.lead }} <code class="ph-chip-code">{{ covers.code }}</code></span>
        </span>
      </button>
      <button
        type="button"
        class="ph-row ph-row--danger"
        :disabled="busy"
        data-testid="permission-deny"
        @click="openDeny"
      >
        <span class="ph-row__main">
          <span class="ph-row__title">Deny</span>
          <span class="ph-row__sub">and tell the agent what to do instead</span>
        </span>
        <ChevronRight
          class="ph-row__chev"
          :size="16"
          :stroke-width="3"
          aria-hidden="true"
        />
      </button>
    </div>
    <div
      v-if="denying"
      ref="denyBox"
    >
      <div class="ph-group-h">
        Tell the agent what to do instead
      </div>
      <div class="ph-sheet__pad">
        <form
          class="ph-composer ph-glass-field"
          @submit.prevent="deny"
        >
          <textarea
            v-model="instead"
            rows="2"
            placeholder="Optional: what to do instead"
            aria-label="What the agent should do instead"
            data-testid="permission-deny-text"
            class="pc__instead"
          />
          <button
            type="submit"
            class="ph-send ph-btn--danger"
            :disabled="busy"
            aria-label="Deny"
            data-testid="permission-deny-send"
          >
            <ArrowUp
              :size="22"
              :stroke-width="2.6"
              aria-hidden="true"
            />
          </button>
        </form>
      </div>
    </div>
  </div>
</template>

<style scoped>
.pc__cmd {
  margin: 10px 0 16px;
}

.pc__detail {
  max-height: 160px;
  margin: -6px 0 16px;
  overflow: auto;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.pc__group {
  margin-top: 16px;
}

.ph-row__title,
.ph-row__sub {
  display: block;
}

.ph-composer .pc__instead {
  height: auto;
  min-height: 62px;
}
</style>
