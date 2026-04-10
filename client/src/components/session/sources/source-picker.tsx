import type { RegisteredSessionSource } from "@/session-sources/types";

interface SourcePickerProps {
  sources: readonly RegisteredSessionSource[];
  selectedSourceId: string | null;
  isLoading: boolean;
  isSourceDisabled: (source: RegisteredSessionSource) => boolean;
  onSelect: (sourceId: string) => void;
}

export function SourcePicker({
  sources,
  selectedSourceId,
  isLoading,
  isSourceDisabled,
  onSelect,
}: SourcePickerProps) {
  return (
    <div className="space-y-1.5">
      <label className="text-sm font-medium" id="source-picker-label">
        Source
      </label>
      <div className="grid grid-cols-1 gap-2" role="radiogroup" aria-labelledby="source-picker-label">
        {sources.map((source) => {
          const isSelected = selectedSourceId === `${source.descriptor.key.providerId}:${source.descriptor.key.sourceType}`;
          const disabled = isLoading || isSourceDisabled(source);

          return (
            <button
              key={`${source.descriptor.key.providerId}:${source.descriptor.key.sourceType}`}
              type="button"
              role="radio"
              aria-checked={isSelected}
              disabled={disabled}
              onClick={() => onSelect(`${source.descriptor.key.providerId}:${source.descriptor.key.sourceType}`)}
              className={`rounded-md border px-3 py-2 text-left transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${
                isSelected
                  ? "border-primary bg-primary/10 text-primary"
                  : "border-input bg-background text-foreground hover:bg-accent hover:text-accent-foreground"
              }`}
            >
              <div className="text-sm font-medium">{source.label}</div>
              {source.description ? (
                <p className="mt-1 text-xs text-muted-foreground">{source.description}</p>
              ) : null}
            </button>
          );
        })}
      </div>
    </div>
  );
}
