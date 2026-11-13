import { onScopeDispose, shallowRef, type Ref } from "vue";
import { useDraftAttachments } from "@/composables/use-draft-attachments";
import { ALLOWED_IMAGE_MIMES, MAX_ATTACHMENTS_PER_PROMPT, MAX_IMAGE_BYTES, sniffImageMime } from "@/lib/image-validation";

const PASTE_ERROR_MS = 5000;

/**
 * The images held for the next prompt of a composer, and the pasted-file error it shows. Shared by the session
 * composer and the new-session page: picking, pasting or dropping an image is checked the same way in both.
 * `key` names the draft the images belong to; `supportsImages` is whether the harness the prompt goes to takes them.
 */
export function useImageAttachments(key: string, supportsImages: Readonly<Ref<boolean>>) {
  const { attachments, addAttachment, removeAttachment, clearAttachments } = useDraftAttachments(key);
  const pasteError = shallowRef<string | undefined>(undefined);
  let pasteErrorTimer: ReturnType<typeof setTimeout> | null = null;

  function clearPasteError(): void {
    pasteError.value = undefined;
    if (pasteErrorTimer) {
      clearTimeout(pasteErrorTimer);
      pasteErrorTimer = null;
    }
  }

  function setPasteError(message: string): void {
    pasteError.value = message;
    if (pasteErrorTimer) clearTimeout(pasteErrorTimer);
    pasteErrorTimer = setTimeout(() => {
      pasteError.value = undefined;
      pasteErrorTimer = null;
    }, PASTE_ERROR_MS);
  }

  /** Holds one image for the prompt, or says why it can't. */
  function addImage(blob: File): void {
    if (!supportsImages.value) {
      setPasteError("This session's harness can't take images.");
      return;
    }
    if (!ALLOWED_IMAGE_MIMES.has(blob.type)) {
      setPasteError(`Unsupported image type: ${blob.type || "unknown"}`);
      return;
    }
    if (blob.size > MAX_IMAGE_BYTES) {
      setPasteError(`Image exceeds 5MB limit (${(blob.size / 1024 / 1024).toFixed(1)}MB)`);
      return;
    }
    if (attachments.value.length >= MAX_ATTACHMENTS_PER_PROMPT) {
      setPasteError(`Too many images (max ${MAX_ATTACHMENTS_PER_PROMPT})`);
      return;
    }

    const reader = new FileReader();
    reader.onload = () => {
      const dataUrl = reader.result as string;
      const data = dataUrl.split(",")[1] ?? "";
      // The type comes from what the bytes are, not the file's name: a PNG called shot.jpg is sent as a PNG.
      const mime = sniffImageMime(data);
      if (!mime) {
        setPasteError(`${blob.name || "That file"} isn't a PNG, JPEG, GIF or WebP picture.`);
        return;
      }
      addAttachment({
        id: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
        mime,
        filename: blob.name || "image.png",
        data,
        previewUrl: URL.createObjectURL(blob),
      });
      clearPasteError();
    };
    reader.readAsDataURL(blob);
  }

  onScopeDispose(clearPasteError);

  return { attachments, addAttachment, removeAttachment, clearAttachments, pasteError, setPasteError, clearPasteError, addImage };
}
