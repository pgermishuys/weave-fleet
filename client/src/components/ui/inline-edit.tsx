"use client";

import { useState, useRef, useEffect, useCallback } from "react";
import { Input } from "@/components/ui/input";
import { cn } from "@/lib/utils";

export interface InlineEditProps {
  value: string;
  onSave: (newValue: string) => void;
  onCancel?: () => void;
  className?: string;
  /** Controlled editing state — allows external triggers (e.g. context menu "Rename") */
  editing?: boolean;
  onEditingChange?: (editing: boolean) => void;
}

export function InlineEdit({
  value,
  onSave,
  onCancel,
  className,
  editing: controlledEditing,
  onEditingChange,
}: InlineEditProps) {
  const [internalEditing, setInternalEditing] = useState(false);

  const isEditing = controlledEditing ?? internalEditing;
  const setIsEditing = useCallback(
    (v: boolean) => {
      if (onEditingChange) {
        onEditingChange(v);
      } else {
        setInternalEditing(v);
      }
    },
    [onEditingChange]
  );
  const [draft, setDraft] = useState(value);
  const inputRef = useRef<HTMLInputElement>(null);
  const committingRef = useRef(false);

  // Keep draft in sync if external value changes while not editing.
  // This uses React's official "adjusting state during rendering" pattern
  // (setState during render with a guard) instead of useEffect to avoid
  // cascading renders. See: https://react.dev/learn/you-might-not-need-an-effect#adjusting-some-state-when-a-prop-changes
  const [prevValue, setPrevValue] = useState(value);
  if (value !== prevValue) {
    setPrevValue(value);
    if (!isEditing) {
      setDraft(value);
    }
  }

  useEffect(() => {
    if (isEditing && inputRef.current) {
      inputRef.current.focus();
      inputRef.current.select();
    }
  }, [isEditing]);

  const handleDoubleClick = useCallback(() => {
    setDraft(value);
    setIsEditing(true);
  }, [value, setIsEditing]);

  const commit = useCallback(() => {
    if (!isEditing) return;
    if (committingRef.current) return;
    committingRef.current = true;

    const trimmed = draft.trim();
    setIsEditing(false);
    committingRef.current = false;

    if (trimmed === "") {
      setDraft(value);
      onCancel?.();
    } else {
      onSave(trimmed);
    }
  }, [isEditing, draft, value, onSave, onCancel, setIsEditing]);

  const cancel = useCallback(() => {
    if (!isEditing) return;
    setIsEditing(false);
    setDraft(value);
    onCancel?.();
  }, [isEditing, value, onCancel, setIsEditing]);

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLInputElement>) => {
      if (e.key === "Enter") {
        e.preventDefault();
        commit();
      } else if (e.key === "Escape") {
        e.preventDefault();
        cancel();
      }
    },
    [commit, cancel]
  );

  // Use requestAnimationFrame delay to handle the blur vs context menu pitfall:
  // When the user right-clicks while the input is focused, blur fires before the
  // context menu registers. We defer the commit so the user can still interact.
  const handleBlur = useCallback(() => {
    requestAnimationFrame(() => {
      commit();
    });
  }, [commit]);

  if (isEditing) {
    return (
      <Input
        ref={inputRef}
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={handleKeyDown}
        onBlur={handleBlur}
        className={cn("h-6 px-1 py-0 text-sm font-medium", className)}
        onClick={(e) => { e.preventDefault(); e.stopPropagation(); }}
      />
    );
  }

  return (
    <span
      onDoubleClick={handleDoubleClick}
      className={cn("cursor-default select-none", className)}
      title="Double-click to rename"
    >
      {value}
    </span>
  );
}
