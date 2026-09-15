<script setup lang="ts">
import { computed, onMounted, shallowRef, useId, watch } from "vue";
import { CalendarClock, Check, ChevronDown, Clock, MessageSquareText, Repeat, Zap } from "lucide-vue-next";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { useAutomationsStore } from "@/stores/automations";
import { describeEventType, EVENT_TYPES } from "@/lib/automations";
import { cronOf, dayName, hm, localDate, nextRun, whenChip, withTime, type Hm, type When } from "@/lib/automation-schedule";

/** The When chip: what the message says, or a schedule, a one-off date, a cron or an event chosen here. */

const props = defineProps<{
  when: When | null;
  /** The schedule came from the words in the message. */
  parsed: boolean;
  /** The message has schedule words that a choice here overrides, so "Use the message" can bring them back. */
  messageHasSchedule: boolean;
  timeZone: string;
  disabled?: boolean;
}>();

const emit = defineEmits<{
  /** A schedule chosen here; null to go back to the words in the message. */
  "update:when": [when: When | null];
  closeAutoFocus: [event: Event];
}>();

const open = defineModel<boolean>("open", { default: false });
const store = useAutomationsStore();
const timeId = useId();
const dateId = useId();
const cronId = useId();
const eventTypes = shallowRef<readonly string[]>(EVENT_TYPES);
const cronDraft = shallowRef("");
const cronError = shallowRef<string | null>(null);

onMounted(async () => {
  try {
    eventTypes.value = await store.fetchEventCatalog();
  } catch {
    // The built-in list is the same one; the server's is only fresher.
  }
});

watch(open, (isOpen) => {
  if (isOpen) {
    cronDraft.value = props.when ? cronOf(props.when) ?? "" : "";
    cronError.value = null;
  }
});

const time = computed<Hm>(() => (props.when && "t" in props.when ? props.when.t : [9, 0]));
const onceDate = computed(() => {
  const when = props.when;
  if (when?.kind !== "once") return "";
  return localDate(nextRun(when)!);
});
const today = new Date().getDay();
/** The weekly option is the schedule's own day when it has one, otherwise today. */
const weeklyDay = computed(() => (props.when?.kind === "weekly" && props.when.days.length === 1 ? props.when.days[0] : today));
const chipLabel = computed(() => whenChip(props.when));

function is(kind: When["kind"], test?: (when: When) => boolean): boolean {
  return props.when?.kind === kind && (!test || test(props.when));
}

function choose(when: When | null): void {
  emit("update:when", when);
  open.value = false;
}

function chooseRepeat(which: "daily" | "weekdays" | "weekly" | "hourly"): void {
  const t = time.value;
  choose(which === "daily" ? { kind: "weekly", days: [0, 1, 2, 3, 4, 5, 6], t }
    : which === "weekdays" ? { kind: "weekly", days: [1, 2, 3, 4, 5], t }
      : which === "weekly" ? { kind: "weekly", days: [weeklyDay.value], t }
        : { kind: "hours", n: 1 });
}

function chooseOnce(): void {
  const tomorrow = new Date();
  tomorrow.setDate(tomorrow.getDate() + 1);
  emit("update:when", { kind: "once", date: onceDate.value || localDate(tomorrow), t: time.value });
}

function setTime(event: Event): void {
  const [h, m] = (event.target as HTMLInputElement).value.split(":").map(Number);
  if (Number.isNaN(h) || Number.isNaN(m)) return;
  const base: When = props.when && "t" in props.when ? props.when : { kind: "weekly", days: [today], t: [9, 0] };
  emit("update:when", withTime(base, [h, m]));
}

function setDate(event: Event): void {
  const value = (event.target as HTMLInputElement).value;
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return;
  emit("update:when", { kind: "once", date: value, t: time.value });
}

function applyCron(): void {
  const expr = cronDraft.value.trim().replace(/\s+/g, " ");
  if (!expr) return;
  if (!/^(\S+ ){4}\S+$/.test(expr)) {
    cronError.value = "A cron has five parts: minute hour day month weekday.";
    return;
  }
  choose({ kind: "cron", expr });
}
</script>

<template>
  <Popover v-model:open="open">
    <PopoverTrigger as-child>
      <button
        type="button"
        class="ns-chip"
        :class="{ 'ns-chip--attention': !when, 'automation-when--parsed': parsed }"
        data-testid="automation-when-chip"
        :disabled="disabled"
      >
        <Zap
          v-if="when?.kind === 'event'"
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <Clock
          v-else
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <span class="ns-chip__label">{{ chipLabel }}</span>
        <ChevronDown
          class="ns-chip__chevron"
          aria-hidden="true"
        />
      </button>
    </PopoverTrigger>
    <PopoverContent
      class="ns-pop automation-when"
      side="top"
      align="start"
      :side-offset="6"
      :collision-padding="8"
      @close-auto-focus="emit('closeAutoFocus', $event)"
    >
      <p
        v-if="parsed"
        class="ns-pop__note"
      >
        Picked up from your message. Choosing here takes over.
      </p>
      <button
        v-else-if="messageHasSchedule"
        type="button"
        class="ns-option"
        data-testid="automation-when-use-message"
        @click="choose(null)"
      >
        <MessageSquareText
          class="ns-option__icon"
          aria-hidden="true"
        />
        <span class="ns-option__text">
          <span class="ns-option__title">Use the words in the message</span>
        </span>
      </button>

      <div class="ns-pop__label">
        Repeat
      </div>
      <button
        type="button"
        class="ns-option"
        @click="chooseRepeat('daily')"
      >
        <Repeat
          class="ns-option__icon"
          aria-hidden="true"
        />
        <span class="ns-option__text">
          <span class="ns-option__title">Every day</span>
          <span class="ns-option__detail">at {{ hm(time) }}</span>
        </span>
        <Check
          v-if="is('weekly', (w) => w.kind === 'weekly' && w.days.length === 7)"
          class="ns-option__check"
          aria-hidden="true"
        />
      </button>
      <button
        type="button"
        class="ns-option"
        @click="chooseRepeat('weekdays')"
      >
        <Repeat
          class="ns-option__icon"
          aria-hidden="true"
        />
        <span class="ns-option__text">
          <span class="ns-option__title">Weekdays</span>
          <span class="ns-option__detail">Mon–Fri at {{ hm(time) }}</span>
        </span>
        <Check
          v-if="is('weekly', (w) => w.kind === 'weekly' && w.days.join() === '1,2,3,4,5')"
          class="ns-option__check"
          aria-hidden="true"
        />
      </button>
      <button
        type="button"
        class="ns-option"
        @click="chooseRepeat('weekly')"
      >
        <Repeat
          class="ns-option__icon"
          aria-hidden="true"
        />
        <span class="ns-option__text">
          <span class="ns-option__title">{{ dayName(weeklyDay) }}s</span>
          <span class="ns-option__detail">every week at {{ hm(time) }}</span>
        </span>
        <Check
          v-if="is('weekly', (w) => w.kind === 'weekly' && w.days.length === 1)"
          class="ns-option__check"
          aria-hidden="true"
        />
      </button>
      <button
        type="button"
        class="ns-option"
        @click="chooseRepeat('hourly')"
      >
        <Repeat
          class="ns-option__icon"
          aria-hidden="true"
        />
        <span class="ns-option__text">
          <span class="ns-option__title">Every hour</span>
          <span class="ns-option__detail">on the hour</span>
        </span>
        <Check
          v-if="is('hours')"
          class="ns-option__check"
          aria-hidden="true"
        />
      </button>

      <div class="ns-pop__separator" />
      <button
        type="button"
        class="ns-option"
        data-testid="automation-when-once"
        @click="chooseOnce"
      >
        <CalendarClock
          class="ns-option__icon"
          aria-hidden="true"
        />
        <span class="ns-option__text">
          <span class="ns-option__title">Once</span>
          <span class="ns-option__detail">on a date you pick, then it switches off</span>
        </span>
        <Check
          v-if="is('once')"
          class="ns-option__check"
          aria-hidden="true"
        />
      </button>
      <div class="automation-when__fields">
        <div
          v-if="when?.kind === 'once'"
          class="ns-field"
        >
          <label
            :for="dateId"
            class="ns-field__label"
          >Date</label>
          <input
            :id="dateId"
            type="date"
            class="ns-field__input"
            :value="onceDate"
            @change="setDate"
          >
        </div>
        <div
          v-if="when?.kind !== 'event' && when?.kind !== 'hours' && when?.kind !== 'minutes' && when?.kind !== 'cron'"
          class="ns-field"
        >
          <label
            :for="timeId"
            class="ns-field__label"
          >Time ({{ timeZone }})</label>
          <input
            :id="timeId"
            type="time"
            class="ns-field__input"
            data-testid="automation-when-time"
            :value="hm(time)"
            @change="setTime"
          >
        </div>
      </div>

      <div class="ns-pop__separator" />
      <div class="ns-field">
        <label
          :for="cronId"
          class="ns-field__label"
        >Custom (cron, in {{ timeZone }} time)</label>
        <input
          :id="cronId"
          v-model="cronDraft"
          type="text"
          class="ns-field__input ns-field__input--mono"
          data-testid="automation-when-cron"
          placeholder="0 9 * * 1"
          autocomplete="off"
          spellcheck="false"
          @keydown.enter.prevent="applyCron"
          @blur="cronDraft.trim() && cronDraft.trim() !== (when ? cronOf(when) : '') && applyCron()"
        >
        <span
          v-if="cronError"
          class="automation-when__error"
          role="alert"
        >{{ cronError }}</span>
      </div>

      <div class="ns-pop__separator" />
      <div class="ns-pop__label">
        When something happens
      </div>
      <button
        v-for="eventType in eventTypes"
        :key="eventType"
        type="button"
        class="ns-option"
        :data-testid="`automation-when-event-${eventType}`"
        @click="choose({ kind: 'event', eventType })"
      >
        <Zap
          class="ns-option__icon"
          aria-hidden="true"
        />
        <span class="ns-option__text">
          <span class="ns-option__title">{{ describeEventType(eventType) }}</span>
        </span>
        <Check
          v-if="is('event', (w) => w.kind === 'event' && w.eventType === eventType)"
          class="ns-option__check"
          aria-hidden="true"
        />
      </button>
    </PopoverContent>
  </Popover>
</template>

<style scoped>
.automation-when {
  width: 300px;
  max-height: min(560px, 70vh);
  overflow-y: auto;
}

.automation-when__fields {
  display: grid;
  gap: 2px;
}

.automation-when__error {
  color: var(--error);
  font-size: 11.5px;
}

/* A schedule read from the message is tinted like its highlight. */
.automation-when--parsed .ns-chip__label {
  color: var(--accent);
}
</style>
