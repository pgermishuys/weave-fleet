import type { RefObject } from "react";
import { FolderOpen, GitBranch } from "lucide-react";
import { Input } from "@/components/ui/input";
import type { ScannedRepository } from "@/lib/api-types";

export type RepositoryIsolationStrategy = "existing" | "worktree";

interface RepositorySourceFormProps {
  isLoading: boolean;
  repositories: readonly ScannedRepository[];
  filteredRepositories: readonly ScannedRepository[];
  selectedRepository: ScannedRepository | null;
  repositorySearch: string;
  repositoryDropdownOpen: boolean;
  repositoryHighlightIndex: number;
  repositoryInputRef: RefObject<HTMLInputElement | null>;
  repositoryListRef: RefObject<HTMLDivElement | null>;
  strategy: RepositoryIsolationStrategy;
  branch: string;
  branchManuallyEdited: boolean;
  onRepositorySearchChange: (value: string) => void;
  onRepositoryFocus: () => void;
  onRepositoryBlur: () => void;
  onRepositoryKeyDown: (event: React.KeyboardEvent<HTMLInputElement>) => void;
  onRepositoryHover: (index: number) => void;
  onRepositorySelect: (repository: ScannedRepository) => void;
  onStrategyChange: (strategy: RepositoryIsolationStrategy) => void;
  onBranchChange: (value: string) => void;
}

const STRATEGY_ORDER: readonly RepositoryIsolationStrategy[] = ["worktree", "existing"];

const STRATEGY_LABELS: Record<RepositoryIsolationStrategy, string> = {
  existing: "Directory",
  worktree: "Worktree",
};

const STRATEGY_DESCRIPTIONS: Record<RepositoryIsolationStrategy, string> = {
  existing: "Use the repository directory as-is. Simple, no copy or branch.",
  worktree: "Creates a git worktree — ideal for parallel work on the same repo.",
};

const STRATEGY_ICONS: Record<RepositoryIsolationStrategy, typeof FolderOpen> = {
  existing: FolderOpen,
  worktree: GitBranch,
};

export function RepositorySourceForm({
  isLoading,
  repositories,
  filteredRepositories,
  selectedRepository,
  repositorySearch,
  repositoryDropdownOpen,
  repositoryHighlightIndex,
  repositoryInputRef,
  repositoryListRef,
  strategy,
  branch,
  branchManuallyEdited,
  onRepositorySearchChange,
  onRepositoryFocus,
  onRepositoryBlur,
  onRepositoryKeyDown,
  onRepositoryHover,
  onRepositorySelect,
  onStrategyChange,
  onBranchChange,
}: RepositorySourceFormProps) {
  return (
    <>
      <div className="space-y-1.5">
        <label className="text-sm font-medium" htmlFor="repo-select">
          Repository
        </label>
        <div className="relative">
          <Input
            ref={repositoryInputRef}
            id="repo-select"
            value={repositorySearch}
            onChange={(event) => onRepositorySearchChange(event.target.value)}
            onFocus={onRepositoryFocus}
            onBlur={onRepositoryBlur}
            onKeyDown={onRepositoryKeyDown}
            placeholder={repositories.length > 0 ? "Type to filter repositories…" : "No repositories available"}
            disabled={isLoading || repositories.length === 0}
            autoComplete="off"
          />
          {repositoryDropdownOpen && filteredRepositories.length > 0 ? (
            <div
              ref={repositoryListRef}
              className="absolute z-50 mt-1 w-full max-h-48 overflow-auto rounded-md border border-input bg-popover shadow-md thin-scrollbar"
            >
              {filteredRepositories.map((repository, index) => (
                <button
                  key={repository.path}
                  type="button"
                  onMouseDown={(event) => {
                    event.preventDefault();
                    onRepositorySelect(repository);
                  }}
                  onMouseEnter={() => onRepositoryHover(index)}
                  className={`w-full px-3 py-2 text-left text-sm transition-colors ${
                    index === repositoryHighlightIndex
                      ? "bg-accent text-accent-foreground"
                      : "text-popover-foreground hover:bg-accent/50"
                  } ${selectedRepository?.path === repository.path ? "font-medium" : ""}`}
                >
                  <div className="font-mono text-xs">{repository.name}</div>
                  <div className="truncate text-[10px] text-muted-foreground">{repository.path}</div>
                </button>
              ))}
            </div>
          ) : null}
        </div>
      </div>

      <div className="space-y-1.5">
        <label className="text-sm font-medium" id="repo-strategy-label">
          Isolation Strategy
        </label>
        <div className="flex gap-1" role="radiogroup" aria-labelledby="repo-strategy-label">
          {STRATEGY_ORDER.map((candidate) => {
            const Icon = STRATEGY_ICONS[candidate];
            const isSelected = candidate === strategy;
            return (
              <button
                key={candidate}
                type="button"
                role="radio"
                aria-checked={isSelected}
                onClick={() => onStrategyChange(candidate)}
                disabled={isLoading}
                className={`flex-1 rounded-md border px-3 py-2 transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${
                  isSelected
                    ? "border-primary bg-primary/10 text-primary"
                    : "border-input bg-background text-muted-foreground hover:bg-accent hover:text-accent-foreground"
                }`}
              >
                <div className="flex flex-col items-center justify-center">
                  <Icon className="h-4 w-4" />
                  <span className="mt-1 text-xs">{STRATEGY_LABELS[candidate]}</span>
                </div>
              </button>
            );
          })}
        </div>
        <p className="text-xs text-muted-foreground">{STRATEGY_DESCRIPTIONS[strategy]}</p>
      </div>

      {strategy === "worktree" ? (
        <div className="space-y-1.5">
          <label className="text-sm font-medium" htmlFor="branch">
            Branch <span className="font-normal text-muted-foreground">(optional)</span>
          </label>
          <Input
            id="branch"
            value={branch}
            onChange={(event) => onBranchChange(event.target.value)}
            placeholder="feature/my-branch"
            disabled={isLoading}
          />
          <p className="text-xs text-muted-foreground">
            {branchManuallyEdited
              ? "A unique branch name will be generated if left blank."
              : "Auto-generated from title. Edit to override."}
          </p>
        </div>
      ) : null}
    </>
  );
}
