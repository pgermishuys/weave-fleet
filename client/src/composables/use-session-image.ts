import { onScopeDispose, shallowRef, toValue, watch, type MaybeRefOrGetter, type ShallowRef } from "vue";
import { apiFetchOn } from "@/lib/api-client";
import { useMachineTarget } from "@/lib/machine-target";

export interface SessionImage {
  /** A blob URL an `<img>` can show; null while loading or when it couldn't be read. */
  src: ShallowRef<string | null>;
  /** The file's size in bytes, once read. */
  bytes: ShallowRef<number | null>;
  /** Why it can't be shown, or null. */
  error: ShallowRef<string | null>;
  /** Read the file again, for when it may have changed on disk. */
  reload: () => void;
}

/**
 * An image in the session's folder, read from the session's machine (`/files/image`) as a blob. A blob
 * rather than the address because another machine wants its token, which an `<img>` can't send, and the
 * blob gives the file's size. Nothing is read while `enabled` is false.
 */
export function useSessionImage(
  sessionId: MaybeRefOrGetter<string>,
  path: MaybeRefOrGetter<string>,
  enabled: MaybeRefOrGetter<boolean>,
): SessionImage {
  const { connection: machine } = useMachineTarget();
  const src = shallowRef<string | null>(null);
  const bytes = shallowRef<number | null>(null);
  const error = shallowRef<string | null>(null);
  let objectUrl: string | null = null;
  let generation = 0;

  function show(url: string | null): void {
    if (objectUrl) URL.revokeObjectURL(objectUrl);
    objectUrl = url;
    src.value = url;
  }

  async function load(): Promise<void> {
    const current = ++generation;
    if (!toValue(enabled)) return;
    const id = toValue(sessionId);
    const file = toValue(path);
    try {
      const response = await apiFetchOn(
        machine,
        `/api/sessions/${encodeURIComponent(id)}/files/image?path=${encodeURIComponent(file)}`,
      );
      if (current !== generation) return;
      if (!response.ok) {
        show(null);
        bytes.value = null;
        error.value = response.status === 404
          ? "This file isn't in the session's folder any more."
          : "The image couldn't be read.";
        return;
      }
      const blob = await response.blob();
      if (current !== generation) return;
      show(URL.createObjectURL(blob));
      bytes.value = blob.size;
      error.value = null;
    } catch {
      if (current === generation) error.value = "The image couldn't be read.";
    }
  }

  watch(() => [toValue(sessionId), toValue(path), toValue(enabled)] as const, () => {
    show(null);
    bytes.value = null;
    error.value = null;
    void load();
  }, { immediate: true });

  onScopeDispose(() => {
    generation++;
    show(null);
  });

  return { src, bytes, error, reload: () => void load() };
}
