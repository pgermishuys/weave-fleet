import { onMounted, onUnmounted } from "vue";
import { Lightbulb } from "lucide-vue-next";
import { onGlobalEvent } from "@/composables/use-signalr-socket";
import type { DomainEvent } from "@/lib/domain-events";
import {
  addMemoryNote,
  forgetMemoryNote,
  MEMORY_SAVED,
  MEMORY_UNDO_MS,
  memoryPlace,
  updateMemoryNote,
  type MemorySavedPayload,
} from "@/lib/agent-memory";
import { useNoticesStore } from "@/stores/notices";

/** A notice nobody saw within this long goes away; the note stays in Settings → Memory. */
const MEMORY_NOTICE_EXPIRES_MS = 2 * 60_000;

/**
 * Takes back a note an agent saved: a new note is forgotten, and a note that replaced another puts the old one back.
 */
export async function undoMemorySave(saved: MemorySavedPayload): Promise<void> {
  const { note, previous } = saved;
  if (previous && previous.id === note.id) {
    await updateMemoryNote(note.id, previous.text);
    return;
  }
  await forgetMemoryNote(note.id);
  if (previous) await addMemoryNote(previous.list, previous.repository, previous.text);
}

/**
 * When an agent saves a note, shows it in a small notice with Undo for six seconds, with the time left as a bar. The
 * note is saved already, so sessions read it straight away; Undo takes it back.
 */
export function useMemoryNotices(): void {
  const notices = useNoticesStore();
  let unsubscribe: (() => void) | null = null;

  function show(saved: MemorySavedPayload): void {
    const id = `memory-saved-${saved.note.id}-${saved.note.updated}`;
    const place = memoryPlace(saved.note, saved.repositoryName);
    notices.post({
      id,
      title: `Remembered for ${place}`,
      body: saved.note.text,
      icon: Lightbulb,
      holdMs: MEMORY_UNDO_MS,
      countdown: true,
      expiresMs: MEMORY_NOTICE_EXPIRES_MS,
      actions: [
        {
          label: "Undo",
          run: async () => {
            try {
              await undoMemorySave(saved);
              notices.remove(id);
            } catch (error) {
              notices.update(id, {
                title: "Couldn't undo it",
                body: error instanceof Error ? error.message : "Forget the note in Settings → Memory instead.",
                actions: [],
              });
            }
          },
        },
      ],
    });
  }

  onMounted(() => {
    unsubscribe = onGlobalEvent("sessions", (event: DomainEvent) => {
      if ((event.type as string) !== MEMORY_SAVED) return;
      const saved = event.payload as unknown as MemorySavedPayload | undefined;
      if (!saved?.note?.id) return;
      show(saved);
    });
  });

  onUnmounted(() => {
    unsubscribe?.();
  });
}
