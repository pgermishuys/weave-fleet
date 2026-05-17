import { reactive, computed, type ComputedRef } from "vue";
import type { ImageAttachment } from "@/lib/api-types";

export interface PendingAttachment extends ImageAttachment {
  id: string;
  previewUrl: string;
}

const attachmentRegistry = reactive<Record<string, PendingAttachment[]>>({});

function ensureEntry(sessionId: string): PendingAttachment[] {
  if (!attachmentRegistry[sessionId]) {
    attachmentRegistry[sessionId] = [];
  }
  return attachmentRegistry[sessionId];
}

export function useDraftAttachments(sessionId: string) {
  const attachments: ComputedRef<PendingAttachment[]> = computed(() => ensureEntry(sessionId));

  function addAttachment(attachment: PendingAttachment): void {
    ensureEntry(sessionId).push(attachment);
  }

  function removeAttachment(id: string): void {
    const list = ensureEntry(sessionId);
    const index = list.findIndex((a) => a.id === id);
    if (index >= 0) {
      const [removed] = list.splice(index, 1);
      URL.revokeObjectURL(removed.previewUrl);
    }
  }

  function clearAttachments(): void {
    const list = ensureEntry(sessionId);
    for (const att of list) {
      URL.revokeObjectURL(att.previewUrl);
    }
    attachmentRegistry[sessionId] = [];
  }

  return {
    attachments,
    addAttachment,
    removeAttachment,
    clearAttachments,
  };
}

export function clearDraftAttachments(sessionId: string): void {
  const list = attachmentRegistry[sessionId];
  if (!list) return;
  for (const att of list) {
    URL.revokeObjectURL(att.previewUrl);
  }
  attachmentRegistry[sessionId] = [];
}
