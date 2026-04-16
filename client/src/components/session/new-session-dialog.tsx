import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { ReactNode } from "react";
import { useNavigate } from "react-router";
import { AlertCircle, CheckCircle2, ExternalLink, Loader2 } from "lucide-react";
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
import { usePersistedState } from "@/hooks/use-persisted-state";
import { useRepositories } from "@/hooks/use-repositories";
import { useProjects } from "@/hooks/use-projects";
import { useSessionSources } from "@/session-sources/use-session-sources";
import { DEFAULT_HARNESS_KEY } from "@/components/settings/harnesses-tab";
import { useAppShell } from "@/contexts/app-shell-context";
import type {
  ProjectResponse,
  ScannedRepository,
  SessionSourceSelection,
} from "@/lib/api-types";
import type { RegisteredSessionSource } from "@/session-sources/types";

const UNGROUPED_VALUE = "__ungrouped__";
const REPOSITORY_SOURCE_ID = "builtin.repository:repository";
const DIRECTORY_SOURCE_ID = "builtin.local:directory";
const MANAGED_SOURCE_ID = "builtin.managed:managed-workspace";

const HARNESS_DISPLAY_NAMES: Record<string, string> = {
  "opencode": "OpenCode",
  "claude-code": "Claude Code",
};

interface NewSessionDialogProps {
  trigger?: ReactNode;
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
  defaultDirectory?: string;
  userProjects?: ProjectResponse[];
  initialSource?: SessionSourceSelection;
  initialTitle?: string;
  initialProjectId?: string;
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

export function NewSessionDialog({ trigger, open: controlledOpen, onOpenChange, defaultDirectory, userProjects: userProjectsProp, initialSource, initialTitle, initialProjectId }: NewSessionDialogProps) {
  const navigate = useNavigate();
  const { clientConfig } = useAppShell();
  const [internalOpen, setInternalOpen] = useState(false);
  const open = controlledOpen ?? internalOpen;

  const [directory, setDirectory] = usePersistedState("weave:new-session:lastDirectory", "");
  const [title, setTitle] = useState("");
  const [branch, setBranch] = useState("");
  const [branchManuallyEdited, setBranchManuallyEdited] = useState(false);
  const [selectedProjectId, setSelectedProjectId] = useState<string>(initialProjectId ?? UNGROUPED_VALUE);
  const [selectedHarness, setSelectedHarness] = useState("");
  const [selectedSourceId, setSelectedSourceId] = useState<string | null>(defaultDirectory ? DIRECTORY_SOURCE_ID : null);

  const [selectedRepo, setSelectedRepo] = useState<ScannedRepository | null>(null);
  const [repoSearch, setRepoSearch] = useState("");
  const [repoDropdownOpen, setRepoDropdownOpen] = useState(false);
  const [repoHighlightIdx, setRepoHighlightIdx] = useState(0);
  const [repoStrategy, setRepoStrategy] = useState<RepositoryIsolationStrategy>("worktree");
  const titleInputRef = useRef<HTMLInputElement>(null);
  const repoInputRef = useRef<HTMLInputElement>(null);
  const repoListRef = useRef<HTMLDivElement>(null);

  const { repositories, isLoading: repositoriesLoading, refresh: refreshRepositories } = useRepositories();
  const { sources, isLoading: sourcesLoading, error: sourcesError } = useSessionSources();
  const { createSession, isLoading, error } = useCreateSession();
  const { addSourceToSession, isLoading: isAddingSource, error: addSourceError } = useAddSourceToSession();
  const { refetch } = useSessionsContext();
  const [defaultHarness] = usePersistedState<string>(DEFAULT_HARNESS_KEY, "opencode");

  const { projects } = useProjects({ enabled: !userProjectsProp });
  const userProjects = userProjectsProp ?? projects.filter((project) => project.type !== "scratch");
  const harnesses = useMemo(() => clientConfig.availableHarnesses.map((type) => ({
    type,
    displayName: HARNESS_DISPLAY_NAMES[type] ?? type,
  })), [clientConfig.availableHarnesses]);
  const showProjectPicker = userProjects.length > 0;
  const showHarnessPicker = harnesses.length >= 2;
  const isCloudMode = clientConfig.cloudMode;

  useEffect(() => {
    if (open) {
      void refreshRepositories();
    }
  }, [open, refreshRepositories]);

  useEffect(() => {
    if (open && defaultDirectory) {
      setDirectory(defaultDirectory);
    }
  }, [defaultDirectory, open, setDirectory]);

  const resolvedHarness = useMemo(() => {
    if (selectedHarness && harnesses.some((h) => h.type === selectedHarness)) {
      return selectedHarness;
    }

    if (harnesses.length === 0) {
      return "";
    }

    const defaultAvailable = harnesses.find((harness) => harness.type === defaultHarness);
    return defaultAvailable?.type ?? harnesses[0]?.type ?? "opencode";
  }, [defaultHarness, harnesses, selectedHarness]);

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

  const resolvedSourceId = useMemo(() => {
    if (selectedSourceId) {
      return selectedSourceId;
    }

    if (sources.length === 0) {
      return null;
    }

    const defaultSource = isCloudMode
      ? sources.find((source) => makeSourceId(source) === MANAGED_SOURCE_ID)
        ?? sources.find((source) => makeSourceId(source) === REPOSITORY_SOURCE_ID)
        ?? sources[0]
      : defaultDirectory
        ? sources.find((source) => makeSourceId(source) === DIRECTORY_SOURCE_ID)
        : sources.find((source) => makeSourceId(source) === REPOSITORY_SOURCE_ID) ?? sources[0];

    return defaultSource ? makeSourceId(defaultSource) : null;
  }, [defaultDirectory, isCloudMode, selectedSourceId, sources]);

  const resolvedBranch = resolvedSourceId === REPOSITORY_SOURCE_ID && !branchManuallyEdited
    ? generateBranchName(title)
    : branch;

  const resolvedRepo = useMemo(() => {
    if (resolvedSourceId !== REPOSITORY_SOURCE_ID || repositoriesLoading || repositories.length === 0) {
      return selectedRepo;
    }

    return selectedRepo ?? repositories[0];
  }, [repositories, repositoriesLoading, selectedRepo, resolvedSourceId]);

  const resolvedRepoSearch = !selectedRepo && resolvedRepo ? resolvedRepo.name : repoSearch;

  const selectedSource = useMemo(
    () => sources.find((source) => makeSourceId(source) === resolvedSourceId) ?? null,
    [resolvedSourceId, sources]
  );

  const isRepositorySource = resolvedSourceId === REPOSITORY_SOURCE_ID;
  const isDirectorySource = resolvedSourceId === DIRECTORY_SOURCE_ID;
  const isManagedSource = resolvedSourceId === MANAGED_SOURCE_ID;
  const visibleSources = useMemo(() => sources.filter((source) => {
    const sourceId = makeSourceId(source);
    if (sourceId === REPOSITORY_SOURCE_ID || sourceId === MANAGED_SOURCE_ID) {
      return true;
    }

    if (sourceId === DIRECTORY_SOURCE_ID) {
      return !isCloudMode;
    }

    return false;
  }), [isCloudMode, sources]);

  const effectiveDirectory = isRepositorySource
    ? resolvedRepo?.path ?? ""
    : directory.trim();

  const effectiveSourceSelection = useMemo<SessionSourceSelection | undefined>(() => {
    if (isRepositorySource && resolvedRepo) {
      return {
        key: {
          providerId: "builtin.repository",
          sourceType: "repository",
          actionId: "start-session",
          contractVersion: 1,
        },
        input: {
          repositoryPath: resolvedRepo.path,
          isolationStrategy: repoStrategy,
          ...(repoStrategy === "worktree" && resolvedBranch.trim() ? { branch: resolvedBranch.trim() } : {}),
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

    if (isCloudMode && isManagedSource) {
      return {
        key: {
          providerId: "builtin.managed",
          sourceType: "managed-workspace",
          actionId: "start-session",
          contractVersion: 1,
        },
        input: {},
      };
    }

    return undefined;
  }, [resolvedBranch, directory, isCloudMode, isDirectorySource, isManagedSource, isRepositorySource, repoStrategy, resolvedRepo]);

  const canSubmit = Boolean((isCloudMode || effectiveDirectory) && selectedSource && effectiveSourceSelection) && !isLoading && !isAddingSource;

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
    setSelectedProjectId(initialProjectId ?? UNGROUPED_VALUE);
    setSelectedSourceId(defaultDirectory && !isCloudMode ? DIRECTORY_SOURCE_ID : null);
  }, [defaultDirectory, isCloudMode, initialProjectId]);

  const setOpen = useCallback((value: boolean) => {
    if (!value) {
      resetState();
    }

    setInternalOpen(value);
    onOpenChange?.(value);
  }, [onOpenChange, resetState]);

  const handleSubmit = useCallback(async (event: React.FormEvent) => {
    event.preventDefault();
    if (!effectiveSourceSelection || isLoading || isAddingSource || (!isCloudMode && !effectiveDirectory)) {
      return;
    }

    try {
      const { instanceId, session } = await createSession(isCloudMode ? undefined : effectiveDirectory, {
        title: (title || initialTitle || "").trim() || undefined,
        source: effectiveSourceSelection,
        harnessType: showHarnessPicker ? resolvedHarness : undefined,
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
    resolvedHarness,
    selectedProjectId,
    setOpen,
    showHarnessPicker,
    title,
    initialTitle,
    initialSource,
    isAddingSource,
    isCloudMode,
  ]);

  const sourceError = error ?? addSourceError ?? sourcesError;

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      {trigger ? <DialogTrigger asChild>{trigger}</DialogTrigger> : null}
      <DialogContent
        data-testid="new-session-dialog"
        className="sm:max-w-md top-[10%] translate-y-0 max-h-[85vh] overflow-y-auto"
        onOpenAutoFocus={(e) => {
          e.preventDefault();
          titleInputRef.current?.focus();
        }}
      >
        <DialogHeader>
          <DialogTitle>New Session</DialogTitle>
        </DialogHeader>

        <form onSubmit={handleSubmit} className="space-y-4">
          <SourcePicker
            sources={visibleSources}
            selectedSourceId={resolvedSourceId}
            isLoading={isLoading || sourcesLoading}
            isSourceDisabled={(source) => makeSourceId(source) === REPOSITORY_SOURCE_ID && repositories.length === 0}
            helperText={isCloudMode ? "Cloud mode uses managed workspaces automatically. Local directories are unavailable." : undefined}
            onSelect={setSelectedSourceId}
          />

          {isRepositorySource ? (
            <RepositorySourceForm
              isLoading={isLoading}
              repositories={repositories}
              filteredRepositories={filteredRepos}
              selectedRepository={resolvedRepo}
              repositorySearch={resolvedRepoSearch}
              repositoryDropdownOpen={repoDropdownOpen}
              repositoryHighlightIndex={repoHighlightIdx}
              repositoryInputRef={repoInputRef}
              repositoryListRef={repoListRef}
              strategy={repoStrategy}
              branch={resolvedBranch}
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

          {isCloudMode && isManagedSource ? (
            <div className="rounded-md border border-border bg-muted/30 px-3 py-2 text-xs text-muted-foreground">
              Managed workspaces are created automatically in cloud mode. Start a basic session without entering a local directory.
            </div>
          ) : null}

          <div className="space-y-1.5">
            <label className="text-sm font-medium" htmlFor="session-title">
              Title <span className="font-normal text-muted-foreground">(optional)</span>
            </label>
            <Input
              ref={titleInputRef}
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
              <Select value={resolvedHarness} onValueChange={setSelectedHarness} disabled={isLoading}>
                <SelectTrigger id="harness-select" className="w-full">
                  <SelectValue placeholder="Select harness" />
                </SelectTrigger>
                <SelectContent>
                  {harnesses.map((harness) => (
                    <SelectItem key={harness.type} value={harness.type}>
                      <span className="flex items-center gap-2">
                        <CheckCircle2 className="h-3 w-3 text-green-500" />
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
              <span>
                {sourceError}
                {isCloudMode && (error ?? addSourceError) ? (
                  <>
                    {" "}
                    <a
                      href="/settings"
                      className="underline underline-offset-2 hover:text-red-700 dark:hover:text-red-300 inline-flex items-center gap-0.5"
                      onClick={() => setOpen(false)}
                    >
                      Add an API key in Settings <ExternalLink className="h-3 w-3" />
                    </a>
                  </>
                ) : null}
              </span>
            </div>
          ) : null}

          <Button type="submit" data-testid="create-session-submit" className="w-full weave-gradient-bg hover:opacity-90 border-0" disabled={!canSubmit}>
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
