<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { GripVertical } from "lucide-vue-next";
import { KIND_LABELS, moveAddress, type MachineAddress, type MachineAddressResult } from "@/lib/machine-routes";

/**
 * A machine's addresses in the order Fleet tries them. Drag a row, or focus its handle and press Alt+Up / Alt+Down
 * (plain Up / Down work too). The new order shows at once; `save` stores it, and a failed save puts the old order
 * back and says why through `failed`. With one address there is nothing to order, so it's shown plainly.
 */

const props = defineProps<{
  name: string;
  addresses: readonly MachineAddress[];
  /** The address this tab uses now. */
  inUse: string;
  save: (urls: string[]) => Promise<void>;
}>();
const emit = defineEmits<{ failed: [message: string] }>();

const order = shallowRef<MachineAddress[]>([...props.addresses]);
const saving = shallowRef(false);
watch(() => props.addresses, (next) => {
  if (!saving.value) order.value = [...next];
});

const announcement = shallowRef("");
const dragFrom = shallowRef<number | null>(null);
const dragOver = shallowRef<number | null>(null);
const handles = new Map<string, HTMLElement>();

const single = computed(() => order.value.length < 2);

const RESULT_LABELS: Record<MachineAddressResult, string> = {
  "ok": "Answered",
  "no-answer": "No answer",
  "other-machine": "Another device",
};

function statusOf(address: MachineAddress): string {
  if (address.url === props.inUse) return "In use";
  return address.lastResult ? RESULT_LABELS[address.lastResult] : "Not tried";
}

async function commit(next: MachineAddress[], moved: MachineAddress): Promise<void> {
  const before = order.value;
  order.value = next;
  const position = next.indexOf(moved) + 1;
  announcement.value = `${KIND_LABELS[moved.kind]} moved to position ${position} of ${next.length}`;
  saving.value = true;
  try {
    await props.save(next.map((address) => address.url));
  } catch (error) {
    order.value = before;
    announcement.value = "The order wasn't saved";
    emit("failed", error instanceof Error ? error.message : String(error));
  } finally {
    saving.value = false;
  }
}

function move(from: number, to: number): void {
  if (to < 0 || to >= order.value.length || to === from || saving.value) return;
  const moved = order.value[from];
  void commit(moveAddress(order.value, from, to), moved).then(() => handles.get(moved.url)?.focus());
}

function onKey(event: KeyboardEvent, index: number): void {
  if (event.key !== "ArrowUp" && event.key !== "ArrowDown") return;
  event.preventDefault();
  move(index, index + (event.key === "ArrowUp" ? -1 : 1));
}

function onDragStart(event: DragEvent, index: number): void {
  if (saving.value) return event.preventDefault();
  dragFrom.value = index;
  if (event.dataTransfer) {
    event.dataTransfer.effectAllowed = "move";
    event.dataTransfer.setData("text/plain", order.value[index].url);
  }
}

function onDragOver(event: DragEvent, index: number): void {
  if (dragFrom.value === null) return;
  event.preventDefault();
  dragOver.value = index;
}

function onDrop(event: DragEvent, index: number): void {
  event.preventDefault();
  const from = dragFrom.value;
  onDragEnd();
  if (from !== null) move(from, index);
}

function onDragEnd(): void {
  dragFrom.value = null;
  dragOver.value = null;
}

function setHandle(url: string, element: unknown): void {
  if (element instanceof HTMLElement) handles.set(url, element);
  else handles.delete(url);
}
</script>

<template>
  <div
    class="routes"
    data-testid="machine-addresses"
  >
    <template v-if="single">
      <p class="routes__note">
        Address
      </p>
      <p
        v-for="address in order"
        :key="address.url"
        class="routes__single"
        data-testid="machine-address-single"
      >
        {{ address.url }}
      </p>
    </template>
    <template v-else>
      <p class="routes__note">
        Fleet tries these in order and goes back to a higher one as soon as it answers again.
      </p>
      <ol
        class="routes__list"
        aria-label="Addresses, in the order Fleet tries them"
      >
        <li
          v-for="(address, index) in order"
          :key="address.url"
          class="route"
          :class="{
            'route--in-use': address.url === inUse,
            'route--dragging': dragFrom === index,
            'route--over': dragOver === index && dragFrom !== index,
          }"
          data-testid="machine-address"
          :data-in-use="address.url === inUse ? 'true' : undefined"
          :draggable="dragFrom === index"
          @dragstart="onDragStart($event, index)"
          @dragover="onDragOver($event, index)"
          @drop="onDrop($event, index)"
          @dragend="onDragEnd"
        >
          <button
            :ref="(element) => setHandle(address.url, element)"
            type="button"
            class="route__handle"
            :aria-label="`${KIND_LABELS[address.kind]}, position ${index + 1} of ${order.length}. Move up or down with Alt and the arrow keys`"
            :title="`Drag to reorder, or press Alt+Up or Alt+Down to move ${KIND_LABELS[address.kind]}`"
            data-testid="machine-address-handle"
            :disabled="saving"
            @mousedown="dragFrom = index"
            @mouseup="dragFrom = null"
            @keydown="onKey($event, index)"
          >
            <GripVertical
              :size="14"
              aria-hidden="true"
            />
          </button>
          <span
            class="route__num"
            aria-hidden="true"
          >{{ index + 1 }}</span>
          <span class="route__kind">{{ KIND_LABELS[address.kind] }}</span>
          <span class="route__url">{{ address.url }}</span>
          <span
            class="route__origin"
            :title="address.learned ? 'The machine told Fleet about this address' : 'The address this machine was added with'"
          >{{ address.learned ? "learned" : "added" }}</span>
          <span
            class="route__status"
            data-testid="machine-address-status"
          >{{ statusOf(address) }}</span>
        </li>
      </ol>
      <p class="routes__foot">
        "Learned" means the machine reported the address itself, the last time Fleet reached it. If the router gives it a new one, Fleet updates the address on its own.
      </p>
    </template>
    <span
      class="sr-only"
      role="status"
      aria-live="polite"
      data-testid="machine-address-announcement"
    >{{ announcement }}</span>
  </div>
</template>

<style scoped>
.routes {
  display: grid;
  gap: 6px;
  margin-top: 6px;
}

.routes__note,
.routes__foot {
  font-size: 11.5px;
  color: var(--muted);
}

.routes__single {
  overflow-wrap: anywhere;
  font-family: var(--font-mono);
  font-size: 11.5px;
  color: var(--text);
}

.routes__list {
  display: grid;
  gap: 2px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.route {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 10px;
  padding: 5px 8px 5px 4px;
  border: 1px solid transparent;
  border-radius: var(--radius-btn);
  font-size: 12px;
}

.route--in-use {
  border-color: var(--border);
  background: var(--accent-dim);
}

.route--dragging {
  opacity: 0.5;
}

.route--over {
  border-color: var(--accent);
}

.route__handle {
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  border-radius: var(--radius-btn);
  color: var(--muted);
  cursor: grab;
}

.route__handle:hover:not(:disabled) {
  color: var(--text);
}

.route__handle:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.route__num {
  width: 12px;
  font-family: var(--font-mono);
  color: var(--muted);
}

.route__kind {
  font-weight: 600;
  color: var(--text);
}

.route__url {
  flex: 1 1 200px;
  min-width: 0;
  overflow-wrap: anywhere;
  font-family: var(--font-mono);
  font-size: 11.5px;
  color: var(--muted);
}

.route__origin {
  font-size: 10.5px;
  color: var(--muted);
}

.route__status {
  min-width: 78px;
  text-align: right;
  font-size: 11.5px;
  color: var(--muted);
}

.route--in-use .route__status {
  font-weight: 600;
  color: var(--running);
}
</style>
