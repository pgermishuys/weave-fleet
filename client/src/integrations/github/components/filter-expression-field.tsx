"use client";

import { useState, useCallback, useRef } from "react";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { X, Search, Loader2 } from "lucide-react";
import { serializeFilterExpression, parseFilterExpression } from "../lib/filter-expression";
import { DEFAULT_ISSUE_FILTER, type IssueFilterState } from "../types";

interface FilterExpressionFieldProps {
  filter: IssueFilterState;
  onChange: (filter: IssueFilterState) => void;
  isSearching?: boolean;
}

export function FilterExpressionField({
  filter,
  onChange,
  isSearching,
}: FilterExpressionFieldProps) {
  const serialized = serializeFilterExpression(filter);
  const [editingValue, setEditingValue] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  // When not editing, display the serialized filter expression directly.
  // When editing, display the user's in-progress text.
  const localValue = editingValue ?? serialized;

  const commit = useCallback(() => {
    setEditingValue(null);
    const parsed = parseFilterExpression(localValue);
    onChange(parsed);
  }, [localValue, onChange]);

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLInputElement>) => {
      if (e.key === "Enter") {
        e.preventDefault();
        commit();
        inputRef.current?.blur();
      } else if (e.key === "Escape") {
        e.preventDefault();
        setEditingValue(null);
        inputRef.current?.blur();
      }
    },
    [commit, serialized]
  );

  const handleClear = useCallback(() => {
    onChange({ ...DEFAULT_ISSUE_FILTER });
    setEditingValue(null);
  }, [onChange]);

  const hasFilters = serialized.length > 0;

  return (
    <div className="relative flex-1">
      <div className="absolute left-2 top-1/2 -translate-y-1/2 text-muted-foreground">
        {isSearching ? (
          <Loader2 className="h-3.5 w-3.5 animate-spin" />
        ) : (
          <Search className="h-3.5 w-3.5" />
        )}
      </div>
      <Input
        ref={inputRef}
        value={localValue}
        onChange={(e) => {
          setEditingValue(e.target.value);
        }}
        onFocus={() => {
          if (editingValue === null) setEditingValue(serialized);
        }}
        onBlur={commit}
        onKeyDown={handleKeyDown}
        placeholder="Filter issues… e.g. is:open label:bug author:octocat"
        className="h-7 pl-7 pr-7 text-xs font-mono"
      />
      {hasFilters && (
        <Button
          variant="ghost"
          size="sm"
          className="absolute right-0.5 top-1/2 -translate-y-1/2 h-5 w-5 p-0"
          onClick={handleClear}
          aria-label="Clear filters"
        >
          <X className="h-3 w-3" />
        </Button>
      )}
    </div>
  );
}
