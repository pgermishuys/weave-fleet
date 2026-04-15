
import { useState } from "react";
import { MessageCircleQuestion, ChevronRight, ChevronDown, Loader2, Check, X } from "lucide-react";
import {
  Collapsible,
  CollapsibleTrigger,
  CollapsibleContent,
} from "@/components/ui/collapsible";
import type { AccumulatedPart } from "@/lib/api-types";

interface QuestionToolCardProps {
  part: AccumulatedPart & { type: "tool" };
}

interface QuestionOption {
  label: string;
  description?: string;
}

interface QuestionPrompt {
  question: string;
  header?: string;
  options?: QuestionOption[];
  multiple?: boolean;
}

export function QuestionToolCard({ part }: QuestionToolCardProps) {
  const [open, setOpen] = useState(true);

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const state = part.state as any;
  const isRunning = state?.status === "running" || !state?.status;
  const isCompleted = state?.status === "completed";
  const isError = state?.status === "error";

  const input = state?.input as { questions?: QuestionPrompt[] } | undefined;
  const output: string = state?.output ? String(state.output) : "";
  const error: string = state?.error ? String(state.error) : "";

  const questions = input?.questions ?? [];
  const firstQuestion = questions[0];
  const compactLabel = firstQuestion?.header ?? firstQuestion?.question ?? "question";

  const hasExpandableContent = questions.length > 0 || error || output;

  return (
    <Collapsible open={open} onOpenChange={setOpen}>
      <CollapsibleTrigger asChild disabled={!hasExpandableContent}>
        <button
          type="button"
          className="flex w-full items-center gap-2 py-0.5 text-xs text-muted-foreground text-left hover:bg-accent/30 rounded-sm px-1 -mx-1 transition-colors disabled:hover:bg-transparent disabled:cursor-default"
        >
          {hasExpandableContent ? (
            open ? (
              <ChevronDown className="h-3 w-3 shrink-0 text-muted-foreground/60" />
            ) : (
              <ChevronRight className="h-3 w-3 shrink-0 text-muted-foreground/60" />
            )
          ) : (
            <span className="inline-block h-3 w-3 shrink-0" />
          )}

          <MessageCircleQuestion className="h-3 w-3 shrink-0 text-blue-400" />
          <span className="flex-1 truncate">{compactLabel}</span>
          <span className="font-mono text-muted-foreground/50 shrink-0">question</span>

          {isRunning && <Loader2 className="h-3 w-3 animate-spin text-muted-foreground shrink-0" />}
          {isCompleted && <Check className="h-3 w-3 text-green-500 shrink-0" />}
          {isError && <X className="h-3 w-3 text-red-500 shrink-0" />}
        </button>
      </CollapsibleTrigger>

      <CollapsibleContent>
        <div className="mt-1 mb-1 ml-4 rounded-md border border-border/60 overflow-hidden bg-muted/20">
          {error ? (
            <div className="px-3 py-2 text-xs text-red-600 dark:text-red-400 whitespace-pre-wrap">
              {error}
            </div>
          ) : (
            <div className="px-3 py-2 space-y-3">
              {questions.map((q, qi) => (
                <div key={qi} data-testid="question-prompt">
                  <div className="text-xs font-medium text-foreground mb-1.5">
                    {q.question}
                  </div>
                  {q.options && q.options.length > 0 && (
                    <div className="space-y-1">
                      {q.options.map((opt, oi) => (
                        <div
                          key={oi}
                          className="flex gap-2 text-xs text-muted-foreground pl-1"
                          data-testid="question-option"
                        >
                          <span className="text-blue-400 shrink-0 font-mono">{oi + 1}.</span>
                          <div>
                            <span className="text-foreground/90 font-medium">{opt.label}</span>
                            {opt.description && (
                              <span className="text-muted-foreground"> — {opt.description}</span>
                            )}
                          </div>
                        </div>
                      ))}
                    </div>
                  )}
                  {!isCompleted && !isError && (
                    <div className="mt-2 text-[10px] text-muted-foreground/60 italic">
                      Reply with your choice to continue
                    </div>
                  )}
                </div>
              ))}
              {isCompleted && output && (
                <div className="text-xs text-muted-foreground border-t border-border/40 pt-2 mt-2">
                  <span className="text-muted-foreground/70 font-medium">Answer: </span>
                  {output}
                </div>
              )}
            </div>
          )}
        </div>
      </CollapsibleContent>
    </Collapsible>
  );
}
