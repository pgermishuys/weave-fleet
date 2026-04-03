"use client";

import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { Textarea } from "@/components/ui/textarea";
import { Button } from "@/components/ui/button";
import { Send, Loader2, AlertCircle, ListPlus, Paperclip, X } from "lucide-react";
import { useAutocomplete } from "@/hooks/use-autocomplete";
import { AutocompletePopup } from "@/components/session/autocomplete-popup";
import { AgentSelector } from "@/components/session/agent-selector";
import { ModelSelector } from "@/components/session/model-selector";
import { MessageQueueIndicator } from "@/components/session/message-queue-indicator";
import { useMessageQueue } from "@/hooks/use-message-queue";
import { ALLOWED_IMAGE_MIMES, MAX_IMAGE_BYTES } from "@/lib/image-validation";
import type { AutocompleteAgent, AvailableProvider, ImageAttachment } from "@/lib/api-types";
import type { SelectedModel } from "@/components/session/model-selector";
import { useDraftState } from "@/hooks/use-draft-state";

// ─── Pending Attachment (client-side, with preview URL) ────────────────────

interface PendingAttachment {
  id: string;
  mime: string;
  filename: string;
  /** Base64-encoded data (no data: prefix) */
  data: string;
  /** Object URL for thumbnail preview (must be revoked) */
  previewUrl: string;
}

// ─── Props ─────────────────────────────────────────────────────────────────

interface PromptInputProps {
  onSend?: (
    text: string,
    agent?: string,
    model?: SelectedModel,
    attachments?: ImageAttachment[]
  ) => Promise<void>;
  disabled?: boolean;
  sendError?: string;
  sessionId?: string;
  instanceId?: string;
  agents?: AutocompleteAgent[];
  selectedAgent?: string | null;
  onAgentChange?: (agent: string | null) => void;
  providers?: AvailableProvider[];
  selectedModel?: SelectedModel | null;
  onModelChange?: (model: SelectedModel | null) => void;
  onFocusRequest?: (focus: () => void) => void;
  /** Current session status — used for message queue (queue on busy, auto-send on idle). */
  sessionStatus?: "idle" | "busy";
}

let nextAttachmentId = 0;

export function PromptInput({
  onSend,
  disabled,
  sendError,
  sessionId = "",
  instanceId = "",
  agents = [],
  selectedAgent = null,
  onAgentChange,
  providers = [],
  selectedModel = null,
  onModelChange,
  onFocusRequest,
  sessionStatus = "idle",
}: PromptInputProps) {
  const { text: persistedDraft, setText: persistDraft, clearDraft } = useDraftState(sessionId);
  const [value, setValue] = useState(persistedDraft);
  const [isSending, setIsSending] = useState(false);
  const [cursorPos, setCursorPos] = useState(0);
  const [pendingAttachments, setPendingAttachments] = useState<PendingAttachment[]>([]);
  const [pasteError, setPasteError] = useState<string | null>(null);
  const [isDragOver, setIsDragOver] = useState(false);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const dropZoneRef = useRef<HTMLDivElement>(null);

  const historyRef = useRef<string[]>([]);
  const historyIndexRef = useRef<number>(-1);

  // Wrapper that updates both local state and debounced persistence
  const setValueAndPersist = useCallback(
    (v: string) => {
      setValue(v);
      persistDraft(v);
    },
    [persistDraft],
  );

  // When sessionId changes, sync the local value from the new session's persisted draft
  const prevSessionIdRef = useRef(sessionId);
  useEffect(() => {
    if (prevSessionIdRef.current !== sessionId) {
      prevSessionIdRef.current = sessionId;
      setValue(persistedDraft);
    }
  }, [sessionId, persistedDraft]);

  const isDisabled = disabled || isSending;

  // Message queue: when session is busy, messages are queued and auto-sent on idle
  const messageQueue = useMessageQueue(sessionStatus, onSend);

  // Re-focus whenever the input becomes enabled (e.g. agent finishes responding)
  useEffect(() => {
    if (!isDisabled) {
      inputRef.current?.focus();
    }
  }, [isDisabled]);

  // Expose a focus callback via onFocusRequest
  useEffect(() => {
    onFocusRequest?.(() => inputRef.current?.focus());
  }, [onFocusRequest]);

  // Cleanup object URLs on unmount
  useEffect(() => {
    return () => {
      // eslint-disable-next-line react-hooks/exhaustive-deps
      pendingAttachments.forEach((a) => URL.revokeObjectURL(a.previewUrl));
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Auto-clear paste error after 5 seconds
  useEffect(() => {
    if (!pasteError) return;
    const timer = setTimeout(() => setPasteError(null), 5000);
    return () => clearTimeout(timer);
  }, [pasteError]);

  const autocomplete = useAutocomplete({
    value,
    setValue: setValueAndPersist,
    instanceId,
    inputRef,
    cursorPosition: cursorPos,
  });

  const canSend =
    (!!value.trim() || pendingAttachments.length > 0) &&
    !autocomplete.isOpen &&
    !isSending &&
    !disabled;

  // Sync cursor position from the textarea element
  const updateCursor = () => {
    setCursorPos(inputRef.current?.selectionStart ?? 0);
  };

  // ─── Image processing helper ─────────────────────────────────────────────

  const processImageBlob = useCallback((blob: File) => {
    if (!ALLOWED_IMAGE_MIMES.has(blob.type)) {
      setPasteError(`Unsupported type: ${blob.type}`);
      return;
    }
    if (blob.size > MAX_IMAGE_BYTES) {
      setPasteError(`Image exceeds 5MB limit (${(blob.size / 1024 / 1024).toFixed(1)}MB)`);
      return;
    }

    const reader = new FileReader();
    reader.onload = () => {
      const dataUrl = reader.result as string;
      const base64 = dataUrl.split(",")[1];
      const ext = blob.type.split("/")[1] ?? "png";
      const id = `att-${++nextAttachmentId}`;
      setPendingAttachments((prev) => [
        ...prev,
        {
          id,
          mime: blob.type,
          filename: blob.name || `pasted-image-${prev.length + 1}.${ext}`,
          data: base64,
          previewUrl: URL.createObjectURL(blob),
        },
      ]);
      setPasteError(null);
    };
    reader.readAsDataURL(blob);
  }, []);

  // ─── Clipboard paste handler ─────────────────────────────────────────────

  const handlePaste = useCallback(
    (e: React.ClipboardEvent<HTMLTextAreaElement>) => {
      const items = Array.from(e.clipboardData.items);
      const imageItems = items.filter((item) => item.type.startsWith("image/"));
      if (imageItems.length === 0) return; // let default text paste through

      e.preventDefault();
      for (const item of imageItems) {
        const blob = item.getAsFile();
        if (!blob) continue;
        processImageBlob(blob);
      }
    },
    [processImageBlob]
  );

  // ─── Drag-and-drop handlers ──────────────────────────────────────────────

  const handleDragOver = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setIsDragOver(true);
  }, []);

  const handleDragLeave = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    // Only leave if we're actually leaving the drop zone (not entering a child)
    if (dropZoneRef.current && !dropZoneRef.current.contains(e.relatedTarget as Node)) {
      setIsDragOver(false);
    }
  }, []);

  const handleDrop = useCallback(
    (e: React.DragEvent) => {
      e.preventDefault();
      e.stopPropagation();
      setIsDragOver(false);

      const files = Array.from(e.dataTransfer.files);
      for (const file of files) {
        if (file.type.startsWith("image/")) {
          processImageBlob(file);
        }
      }
    },
    [processImageBlob]
  );

  // ─── File input handler ──────────────────────────────────────────────────

  const handleFileSelect = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const files = e.target.files;
      if (!files) return;
      for (const file of Array.from(files)) {
        processImageBlob(file);
      }
      // Reset so the same file can be selected again
      e.target.value = "";
    },
    [processImageBlob]
  );

  // ─── Remove attachment ───────────────────────────────────────────────────

  const removeAttachment = useCallback((id: string) => {
    setPendingAttachments((prev) => {
      const att = prev.find((a) => a.id === id);
      if (att) URL.revokeObjectURL(att.previewUrl);
      return prev.filter((a) => a.id !== id);
    });
  }, []);

  // ─── Mobile keyboard handling ────────────────────────────────────────────
  // When the virtual keyboard opens on mobile, visualViewport shrinks.
  // We scroll the textarea into view so it's not hidden behind the keyboard.
  useEffect(() => {
    const vv = typeof window !== "undefined" ? window.visualViewport : null;
    if (!vv) return;
    const handleResize = () => {
      // If the input is focused, scroll it into view after the viewport resizes
      if (document.activeElement === inputRef.current) {
        setTimeout(() => {
          inputRef.current?.scrollIntoView({ block: "nearest", behavior: "smooth" });
        }, 50);
      }
    };
    vv.addEventListener("resize", handleResize);
    return () => vv.removeEventListener("resize", handleResize);
  }, []);
  const maxHeight = 150;

  useLayoutEffect(() => {
    const textarea = inputRef.current;
    if (!textarea) return;
    textarea.style.height = "auto";
    const newHeight = Math.min(textarea.scrollHeight, maxHeight);
    textarea.style.height = newHeight + "px";
    textarea.style.overflowY = textarea.scrollHeight > maxHeight ? "auto" : "hidden";
  }, [value]);

  // ─── Send logic ──────────────────────────────────────────────────────────
  const handleSend = async () => {
    if (autocomplete.isOpen) return;
    if (!canSend || isDisabled) return;
    const text = value.trim();

    // Push to history (only if there's text)
    if (text) {
      historyRef.current.push(text);
      historyIndexRef.current = -1;
    }

    // Build attachments payload
    const attachments: ImageAttachment[] | undefined =
      pendingAttachments.length > 0
        ? pendingAttachments.map((a) => ({
            mime: a.mime,
            filename: a.filename,
            data: a.data,
          }))
        : undefined;

    // If session is busy, queue the message instead of sending directly
    // Note: queue doesn't support attachments yet — send text only
    if (sessionStatus === "busy" && !attachments) {
      messageQueue.enqueue(
        text,
        selectedAgent ?? undefined,
        selectedModel ?? undefined
      );
      setValue("");
      return;
    }

    // Clear state before send
    setValue("");
    clearDraft();
    setPendingAttachments((prev) => {
      prev.forEach((a) => URL.revokeObjectURL(a.previewUrl));
      return [];
    });
    setIsSending(true);
    try {
      await onSend?.(text, selectedAgent ?? undefined, selectedModel ?? undefined, attachments);
    } finally {
      setIsSending(false);
      inputRef.current?.focus();
    }
  };

  // ─── Keyboard handling ───────────────────────────────────────────────────
  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    // Enter without Shift: send (unless autocomplete is open)
    if (e.key === "Enter" && !e.shiftKey) {
      if (!autocomplete.isOpen) {
        e.preventDefault();
        void handleSend();
        return;
      }
    }

    // ArrowUp: recall previous history entry when value is empty
    if (e.key === "ArrowUp" && !autocomplete.isOpen && value === "") {
      const history = historyRef.current;
      if (history.length === 0) return;
      e.preventDefault();
      const newIndex =
        historyIndexRef.current === -1
          ? history.length - 1
          : Math.max(0, historyIndexRef.current - 1);
      historyIndexRef.current = newIndex;
      setValueAndPersist(history[newIndex]);
      return;
    }

    // ArrowDown: walk forward through history when browsing
    if (e.key === "ArrowDown" && !autocomplete.isOpen && historyIndexRef.current !== -1) {
      e.preventDefault();
      const history = historyRef.current;
      const newIndex = historyIndexRef.current + 1;
      if (newIndex >= history.length) {
        historyIndexRef.current = -1;
        setValueAndPersist("");
      } else {
        historyIndexRef.current = newIndex;
        setValueAndPersist(history[newIndex]);
      }
      return;
    }

    // Delegate all other keys to autocomplete
    autocomplete.onKeyDown(e);
  };

  return (
    <div
      ref={dropZoneRef}
      className={`relative border-t p-3 space-y-2 transition-colors ${
        isDragOver ? "bg-primary/5 border-t-primary/40" : ""
      }`}
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
    >
      {/* Autocomplete popup — floats above the input */}
      <AutocompletePopup
        open={autocomplete.isOpen}
        items={autocomplete.items}
        isLoading={autocomplete.isLoading}
        selectedValue={autocomplete.selectedValue}
        error={autocomplete.error}
        onSelect={autocomplete.onSelect}
      />

      {/* Message queue indicator — shown when messages are queued */}
      <MessageQueueIndicator
        queue={messageQueue.queue}
        onRemove={messageQueue.removeAt}
        onClear={messageQueue.clear}
        isAutoSending={messageQueue.isAutoSending}
      />

      {/* Attachment preview strip */}
      {pendingAttachments.length > 0 && (
        <div className="flex gap-2 flex-wrap px-1">
          {pendingAttachments.map((att) => (
            <div key={att.id} className="relative group">
              {/* eslint-disable-next-line @next/next/no-img-element */}
              <img
                src={att.previewUrl}
                alt={att.filename}
                className="h-16 w-16 rounded-md object-cover border border-border"
              />
              <button
                type="button"
                onClick={() => removeAttachment(att.id)}
                className="absolute -top-1.5 -right-1.5 h-4 w-4 rounded-full bg-destructive text-destructive-foreground flex items-center justify-center opacity-0 group-hover:opacity-100 transition-opacity"
                aria-label={`Remove ${att.filename}`}
              >
                <X className="h-2.5 w-2.5" />
              </button>
              <span className="text-[9px] text-muted-foreground truncate block w-16 text-center mt-0.5">
                {att.filename}
              </span>
            </div>
          ))}
        </div>
      )}

      {(sendError || pasteError) && (
        <div className="flex items-center gap-2 rounded-md bg-red-500/10 border border-red-500/20 px-3 py-1.5 text-xs text-red-600 dark:text-red-400">
          <AlertCircle className="h-3.5 w-3.5 shrink-0" />
          <span>{pasteError ?? sendError}</span>
        </div>
      )}

      {/* Drag overlay hint */}
      {isDragOver && (
        <div className="absolute inset-0 z-10 flex items-center justify-center bg-background/80 border-2 border-dashed border-primary/40 rounded-md pointer-events-none">
          <span className="text-sm text-primary font-medium">Drop image to attach</span>
        </div>
      )}

      <form
        onSubmit={(e) => {
          e.preventDefault();
        }}
        className="flex items-end gap-2"
      >
        {agents.length > 0 && (
          <AgentSelector
            agents={agents}
            selectedAgent={selectedAgent}
            onSelect={onAgentChange ?? (() => {})}
            disabled={isDisabled}
          />
        )}
        {providers.length > 0 && (
          <ModelSelector
            providers={providers}
            selectedModel={selectedModel}
            onSelect={onModelChange ?? (() => {})}
            disabled={isDisabled}
          />
        )}
        <Textarea
          ref={inputRef}
          rows={1}
          value={value}
          onChange={(e) => {
            setValueAndPersist(e.target.value);
            setCursorPos(e.target.selectionStart ?? 0);
            historyIndexRef.current = -1;
          }}
          onClick={updateCursor}
          onSelect={updateCursor}
          onKeyDown={handleKeyDown}
          onPaste={handlePaste}
          onBlur={() => {
            // Delay closing to allow click-to-select on popup items
            setTimeout(() => {
              autocomplete.onClose();
            }, 150);
          }}
          placeholder={sessionStatus === "busy" ? "Queue a follow-up message…" : "Send a message to this session…"}
          className="text-sm"
          disabled={isDisabled}
          style={{ overflowY: "hidden" }}
          // Accessibility attributes for combobox pattern
          role="combobox"
          aria-expanded={autocomplete.isOpen}
          aria-haspopup="listbox"
          aria-autocomplete="list"
          aria-controls="autocomplete-listbox"
          aria-activedescendant={
            autocomplete.isOpen && autocomplete.selectedValue
              ? `autocomplete-item-${autocomplete.selectedValue}`
              : undefined
          }
          autoComplete="off"
        />

        {/* Hidden file input for the attach button */}
        <input
          ref={fileInputRef}
          type="file"
          accept="image/png,image/jpeg,image/gif,image/webp"
          multiple
          className="hidden"
          onChange={handleFileSelect}
        />

        {/* Attach button */}
        <Button
          type="button"
          size="icon"
          variant="ghost"
          disabled={isDisabled}
          onClick={() => fileInputRef.current?.click()}
          title="Attach image"
          className="shrink-0"
        >
          <Paperclip className="h-4 w-4" />
        </Button>

        {/* Send / Queue button */}
        <Button
          type="button"
          size="icon"
          variant="default"
          disabled={!canSend}
          onClick={() => void handleSend()}
          title={sessionStatus === "busy" ? "Queue message" : "Send message"}
        >
          {isSending ? (
            <Loader2 className="h-4 w-4 animate-spin" />
          ) : sessionStatus === "busy" ? (
            <ListPlus className="h-4 w-4" />
          ) : (
            <Send className="h-4 w-4" />
          )}
        </Button>
      </form>
    </div>
  );
}
