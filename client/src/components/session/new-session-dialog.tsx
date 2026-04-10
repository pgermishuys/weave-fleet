import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { ReactNode } from "react";
import { useNavigate } from "react-router";
import { AlertCircle, CheckCircle2, Loader2, XCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { SourcePicker } from "@/components/session/sources/source-picker";
import {
  RepositorySourceForm,
  type RepositoryIsolationStrategy,
} from "@/components/session/sources/repository-source-form";
import { DirectorySourceForm } from "@/components/session/sources/directory-source-form";
import { useCreateSession } from "@/hooks/use-create-session";
import { useAddSourceToSession } from "@/hooks/use-add-source-to-session";
import { useSessionsContext } from "@/contexts/sessions-context";
import { useHarnesses } from "@/hooks/use-harnesses";
import { usePersistedState } from "@/hooks/use-persisted-state";
import { useRepositories } from "@/hooks/use-repositories";
import { useProjects } from "@/hooks/use-projects";
import { useSessionSources } from "@/session-sources/use-session-sources";
import { DEFAULT_HARNESS_KEY } from "@/components/settings/harnesses-tab";
import type {
  ProjectResponse,
  ScannedRepository,
  SessionSourceSelection,
} from "@/lib/api-types";
import type { RegisteredSessionSource } from "@/session-sources/types";

const UNGROUPED_VALUE = "__ungrouped__";
const REPOSITORY_SOURCE_ID = "builtin.repository:repository";
const DIRECTORY_SOURCE_ID = "builtin.local:directory";

interface NewSessionDialogProps {
  trigger?: ReactNode;
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
  defaultDirectory?: string;
  userProjects?: ProjectResponse[];
  initialSource?: SessionSourceSelection;
  initialTitle?: string;
}

function makeSourceId(source: RegisteredSessionSource): string {
  return `${source.descriptor.key.providerId}:${source.descriptor.key.sourceType}`;
}

function generateBranchName(text: string): string {
  return text
    .toLowerCase()
    .trim()
    .replace(/[^a-z0-9\s-]/g, "")
    .replace(/\s+/g, "-")
    .replace(/-+/g, "-")
    .replace(/^-|-$/g, "");
}

export function NewSessionDialog({ trigger, open: controlledOpen, onOpenChange, defaultDirectory, userProjects: userProjectsProp, initialSource, initialTitle }: NewSessionDialogProps) {
  const navigate = useNavigate();
  const [internalOpen, setInternalOpen] = useState(false);
  const open = controlledOpen ?? internalOpen;

  const [directory, setDirectory] = usePersistedState("weave:new-session:lastDirectory", "");
  const [title, setTitle] = useState("");
  const [branch, setBranch] = useState("");
  const [branchManuallyEdited, setBranchManuallyEdited] = useState(false);
  const [selectedProjectId, setSelectedProjectId] = useState<string>(UNGROUPED_VALUE);
  const [selectedHarness, setSelectedHarness] = useState("");
  const [selectedSourceId, setSelectedSourceId] = useState<string | null>(defaultDirectory ? DIRECTORY_SOURCE_ID : null);

  const [selectedRepo, setSelectedRepo] = useState<ScannedRepository | null>(null);
  const [repoSearch, setRepoSearch] = useState("");
  const [repoDropdownOpen, setRepoDropdownOpen] = useState(false);
  const [repoHighlightIdx, setRepoHighlightIdx] = useState(0);
  const [repoStrategy, setRepoStrategy] = useState<RepositoryIsolationStrategy>("worktree");
  const repoInputRef = useRef<HTMLInputElement>(null);
  const repoListRef = useRef<HTMLDivElement>(null);

  const { repositories, isLoading: repositoriesLoading, refresh: refreshRepositories } = useRepositories();
  const { sources, isLoading: sourcesLoading, error: sourcesError } = useSessionSources();
  const { createSession, isLoading, error } = useCreateSession();
  const { addSourceToSession, isLoading: isAddingSource, error: addSourceError } = useAddSourceToSession();
  const { refetch } = useSessionsContext();
  const { harnesses, isLoading: harnessesLoading } = useHarnesses();
  const [defaultHarness] = usePersistedState<string>(DEFAULT_HARNESS_KEY, "opencode");

  const { projects } = useProjects({ enabled: !userProjectsProp });
  const userProjects = userProjectsProp ?? projects.filter((project) => project.type !== "scratch");
  const showProjectPicker = userProjects.length > 0;
  const showHarnessPicker = harnesses.length >= 2;
  const availableHarnesses = harnesses.filter((harness) => harness.available);

  useEffect(() => {
    if (open) {
      void refreshRepositories();
    }
  }, [open, refreshRepositories]);

  useEffect(() => {
    if (!selectedHarness && harnesses.length > 0) {
      const defaultAvailable = availableHarnesses.find((harness) => harness.type === defaultHarness);
      setSelectedHarness(defaultAvailable?.type ?? availableHarnesses[0]?.type ?? "opencode");
    }
  }, [availableHarnesses, defaultHarness, harnesses, selectedHarness]);

  const filteredRepos = useMemo(() => {
    if (!repoSearch.trim()) {
      return repositories;
    }

    const query = repoSearch.toLowerCase();
    return repositories.filter(
      (repo) => repo.name.toLowerCase().includes(query) || repo.path.toLowerCase().includes(query)
    );
  }, [repositories, repoSearch]);

  useEffect(() => {
    if (repoDropdownOpen && repoListRef.current) {
      const highlighted = repoListRef.current.children[repoHighlightIdx] as HTMLElement | undefined;
      highlighted?.scrollIntoView({ block: "nearest" });
    }
  }, [repoDropdownOpen, repoHighlightIdx]);

  useEffect(() => {
    if (open && defaultDirectory) {
      setDirectory(defaultDirectory);
      setSelectedSourceId(DIRECTORY_SOURCE_ID);
    }
  }, [defaultDirectory, open, setDirectory]);

  useEffect(() => {
    if (!open) {
      return;
    }

    if (initialTitle && !title) {
      setTitle(initialTitle);
    }
  }, [initialTitle, open, title]);

  useEffect(() => {
    if (selectedSourceId !== REPOSITORY_SOURCE_ID || branchManuallyEdited) {
      return;
    }

    setBranch(generateBranchName(title));
  }, [branchManuallyEdited, selectedSourceId, title]);

  useEffect(() => {
    if (selectedSourceId !== REPOSITORY_SOURCE_ID || repositoriesLoading || selectedRepo || repositories.length === 0) {
      return;
    }

    setSelectedRepo(repositories[0]);
    setRepoSearch(repositories[0].name);
  }, [repositories, repositoriesLoading, selectedRepo, selectedSourceId]);

  useEffect(() => {
    if (selectedSourceId || sources.length === 0) {
      return;
    }

    const defaultSource = defaultDirectory
      ? sources.find((source) => makeSourceId(source) === DIRECTORY_SOURCE_ID)
      : sources.find((source) => makeSourceId(source) === REPOSITORY_SOURCE_ID) ?? sources[0];

    if (defaultSource) {
      setSelectedSourceId(makeSourceId(defaultSource));
    }
  }, [defaultDirectory, selectedSourceId, sources]);

  const selectedSource = useMemo(
    () => sources.find((source) => makeSourceId(source) === selectedSourceId) ?? null,
    [selectedSourceId, sources]
  );

  const isRepositorySource = selectedSourceId === REPOSITORY_SOURCE_ID;
  const isDirectorySource = selectedSourceId === DIRECTORY_SOURCE_ID;

  const effectiveDirectory = isRepositorySource
    ? selectedRepo?.path ?? ""
    : directory.trim();

  const effectiveSourceSelection = useMemo<SessionSourceSelection | undefined>(() => {
    if (isRepositorySource && selectedRepo) {
      return {
        key: {
          providerId: "builtin.repository",
          sourceType: "repository",
          actionId: "start-session",
          contractVersion: 1,
        },
        input: {
          repositoryPath: selectedRepo.path,
          isolationStrategy: repoStrategy,
          ...(repoStrategy === "worktree" && branch.trim() ? { branch: branch.trim() } : {}),
        },
      };
    }

    if (isDirectorySource && directory.trim()) {
      return {
        key: {
          providerId: "builtin.local",
          sourceType: "directory",
          actionId: "start-session",
          contractVersion: 1,
        },
        input: {
          directory: directory.trim(),
          isolationStrategy: "existing",
        },
      };
    }

    return undefined;
  }, [branch, directory, isDirectorySource, isRepositorySource, repoStrategy, selectedRepo]);

  const handleRepoSelect = useCallback((repo: ScannedRepository) => {
    setSelectedRepo(repo);
    setRepoSearch(repo.name);
    setRepoDropdownOpen(false);
  }, []);

  const handleRepoBlur = useCallback(() => {
    setTimeout(() => {
      if (filteredRepos.length === 1) {
        handleRepoSelect(filteredRepos[0]);
      } else if (selectedRepo && repoSearch !== selectedRepo.name) {
        const stillMatches = filteredRepos.find((repo) => repo.path === selectedRepo.path);
        if (stillMatches) {
          setRepoSearch(selectedRepo.name);
        } else {
          setSelectedRepo(null);
        }
      }

      setRepoDropdownOpen(false);
    }, 150);
  }, [filteredRepos, handleRepoSelect, repoSearch, selectedRepo]);

  const handleRepoKeyDown = useCallback((event: React.KeyboardEvent<HTMLInputElement>) => {
    if (!repoDropdownOpen && (event.key === "ArrowDown" || event.key === "ArrowUp")) {
      setRepoDropdownOpen(true);
      event.preventDefault();
      return;
    }

    if (event.key === "ArrowDown") {
      event.preventDefault();
      setRepoHighlightIdx((current) => Math.min(current + 1, filteredRepos.length - 1));
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      setRepoHighlightIdx((current) => Math.max(current - 1, 0));
    } else if (event.key === "Enter" && repoDropdownOpen) {
      event.preventDefault();
      const highlighted = filteredRepos[repoHighlightIdx];
      if (highlighted) {
        handleRepoSelect(highlighted);
      }
    }
  }, [filteredRepos, handleRepoSelect, repoDropdownOpen, repoHighlightIdx]);

  const resetState = useCallback(() => {
    setTitle("");
    setBranch("");
    setBranchManuallyEdited(false);
    setSelectedRepo(null);
    setRepoSearch("");
    setRepoDropdownOpen(false);
    setRepoStrategy("worktree");
    setSelectedHarness("");
    setSelectedProjectId(UNGROUPED_VALUE);
    setSelectedSourceId(defaultDirectory ? DIRECTORY_SOURCE_ID : null);
  }, [defaultDirectory]);

  const setOpen = useCallback((value: boolean) => {
    if (!value) {
      resetState();
    }

    setInternalOpen(value);
    onOpenChange?.(value);
  }, [onOpenChange, resetState]);

  const handleSubmit = useCallback(async (event: React.FormEvent) => {
    event.preventDefault();
    if (!effectiveDirectory || !effectiveSourceSelection || isLoading || isAddingSource) {
      return;
    }

    try {
      const { instanceId, session } = await createSession(effectiveDirectory, {
        title: title.trim() || undefined,
        source: effectiveSourceSelection,
        harnessType: showHarnessPicker ? selectedHarness : undefined,
        projectId: selectedProjectId !== UNGROUPED_VALUE ? selectedProjectId : undefined,
      });

      if (initialSource) {
        await addSourceToSession(session.id, initialSource, true);
      }

      setOpen(false);
      refetch();
      navigate(`/sessions/${encodeURIComponent(session.id)}?instanceId=${encodeURIComponent(instanceId)}`);
    } catch {
      // handled by hook state
    }
  }, [
    createSession,
    effectiveDirectory,
    effectiveSourceSelection,
    isLoading,
    navigate,
    refetch,
    addSourceToSession,
    selectedHarness,
    selectedProjectId,
    setOpen,
    showHarnessPicker,
    title,
    initialSource,
    isAddingSource,
  ]);

  const sourceError = error ?? addSourceError ?? sourcesError;

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      {trigger ? <DialogTrigger asChild>{trigger}</DialogTrigger> : null}
      <DialogContent data-testid="new-session-dialog" className="sm:max-w-md top-[10%] translate-y-0 max-h-[85vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>New Session</DialogTitle>
        </DialogHeader>

        <form onSubmit={handleSubmit} className="space-y-4">
          <SourcePicker
            sources={sources.filter((source) => {
              const sourceId = makeSourceId(source);
              return sourceId === REPOSITORY_SOURCE_ID || sourceId === DIRECTORY_SOURCE_ID;
            })}
            selectedSourceId={selectedSourceId}
            isLoading={isLoading || sourcesLoading}
            isSourceDisabled={(source) => makeSourceId(source) === REPOSITORY_SOURCE_ID && repositories.length === 0}
            onSelect={setSelectedSourceId}
          />

          {isRepositorySource ? (
            <RepositorySourceForm
              isLoading={isLoading}
              repositories={repositories}
              filteredRepositories={filteredRepos}
              selectedRepository={selectedRepo}
              repositorySearch={repoSearch}
              repositoryDropdownOpen={repoDropdownOpen}
              repositoryHighlightIndex={repoHighlightIdx}
              repositoryInputRef={repoInputRef}
              repositoryListRef={repoListRef}
              strategy={repoStrategy}
              branch={branch}
              branchManuallyEdited={branchManuallyEdited}
              onRepositorySearchChange={(value) => {
                setRepoSearch(value);
                setRepoDropdownOpen(true);
                setRepoHighlightIdx(0);
                if (selectedRepo && value !== selectedRepo.name) {
                  setSelectedRepo(null);
                }
              }}
              onRepositoryFocus={() => setRepoDropdownOpen(true)}
              onRepositoryBlur={handleRepoBlur}
              onRepositoryKeyDown={handleRepoKeyDown}
              onRepositoryHover={setRepoHighlightIdx}
              onRepositorySelect={handleRepoSelect}
              onStrategyChange={setRepoStrategy}
              onBranchChange={(value) => {
                setBranch(value);
                setBranchManuallyEdited(true);
              }}
            />
          ) : null}

          {isDirectorySource ? (
            <DirectorySourceForm
              directory={directory}
              isLoading={isLoading}
              onDirectoryChange={setDirectory}
            />
          ) : null}

          <div className="space-y-1.5">
            <label className="text-sm font-medium" htmlFor="session-title">
              Title <span className="font-normal text-muted-foreground">(optional)</span>
            </label>
            <Input
              id="session-title"
              value={title}
              onChange={(event) => setTitle(event.target.value)}
              placeholder="What are you working on?"
              disabled={isLoading}
            />
          </div>

          {showProjectPicker ? (
            <div className="space-y-1.5">
              <label className="text-sm font-medium" htmlFor="project-select">
                Project <span className="font-normal text-muted-foreground">(optional)</span>
              </label>
              <Select value={selectedProjectId} onValueChange={setSelectedProjectId} disabled={isLoading}>
                <SelectTrigger id="project-select" className="w-full">
                  <SelectValue placeholder="Ungrouped" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={UNGROUPED_VALUE}>Ungrouped</SelectItem>
                  {userProjects.map((project) => (
                    <SelectItem key={project.id} value={project.id}>
                      {project.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          ) : null}

          {showHarnessPicker ? (
            <div className="space-y-1.5">
              <label className="text-sm font-medium" htmlFor="harness-select">
                Harness
              </label>
              <Select value={selectedHarness} onValueChange={setSelectedHarness} disabled={isLoading || harnessesLoading}>
                <SelectTrigger id="harness-select" className="w-full">
                  <SelectValue placeholder="Select harness" />
                </SelectTrigger>
                <SelectContent>
                  {harnesses.map((harness) => (
                    <SelectItem key={harness.type} value={harness.type} disabled={!harness.available}>
                      <span className="flex items-center gap-2">
                        {harness.available ? (
                          <CheckCircle2 className="h-3 w-3 text-green-500" />
                        ) : (
                          <XCircle className="h-3 w-3 text-muted-foreground" />
                        )}
                        {harness.displayName}
                      </span>
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          ) : null}

          {sourceError ? (
            <div className="flex items-start gap-2 rounded-md border border-red-500/20 bg-red-500/10 px-3 py-2 text-xs text-red-600 dark:text-red-400">
              <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
              <span>{sourceError}</span>
            </div>
          ) : null}

          <Button type="submit" data-testid="create-session-submit" className="w-full weave-gradient-bg hover:opacity-90 border-0" disabled={!effectiveDirectory || !selectedSource || isLoading || isAddingSource}>
            {isLoading || isAddingSource ? (
              <>
                <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                {isAddingSource ? "Adding context…" : "Spawning…"}
              </>
            ) : (
              "Create Session"
            )}
          </Button>
        </form>
      </DialogContent>
    </Dialog>
  );
}
