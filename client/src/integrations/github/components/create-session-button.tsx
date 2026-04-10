import { useCallback, useEffect, useMemo, useState } from "react";
import { useNavigate, useParams } from "react-router";
import { AlertCircle, Loader2, PlusSquare, Rocket } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Badge } from "@/components/ui/badge";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { useCreateSession } from "@/hooks/use-create-session";
import { useAddSourceToSession } from "@/hooks/use-add-source-to-session";
import { useSessionsContext } from "@/contexts/sessions-context";
import { NewSessionDialog } from "@/components/session/new-session-dialog";
import type { ContextSource } from "@/integrations/types";
import type { SessionListItem, SessionSourcePreview } from "@/lib/api-types";

interface CreateSessionButtonProps {
  contextSource: ContextSource;
  directory?: string;
}

function getContextLabel(contextSource: ContextSource): string {
  return contextSource.type === "github-issue" ? "GitHub Issue" : "GitHub PR";
}

function buildSessionLocation(session: SessionListItem): string {
  return `/sessions/${encodeURIComponent(session.session.id)}?instanceId=${encodeURIComponent(session.instanceId)}`;
}

export function CreateSessionButton({ contextSource, directory }: CreateSessionButtonProps) {
  const { id: currentSessionIdParam } = useParams();
  const navigate = useNavigate();
  const { sessions, refetch } = useSessionsContext();
  const { createSession, isLoading: isCreatingSession, error: createError } = useCreateSession();
  const {
    previewSource,
    addSourceToSession,
    isLoading: isUpdatingSession,
    error: addError,
  } = useAddSourceToSession();
  const [open, setOpen] = useState(false);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [selectedSessionId, setSelectedSessionId] = useState<string>("");
  const [preview, setPreview] = useState<SessionSourcePreview | null>(null);
  const [previewedSessionId, setPreviewedSessionId] = useState<string | null>(null);

  const availableSessions = useMemo(
    () => sessions.filter((session) =>
      session.retentionStatus !== "archived"
      && session.typedInstanceStatus === "running"
      && session.lifecycleStatus === "running"
    ),
    [sessions]
  );

  const currentSessionId = currentSessionIdParam ? decodeURIComponent(currentSessionIdParam) : "";
  const preferredSession = useMemo(
    () => availableSessions.find((session) => session.session.id === currentSessionId) ?? availableSessions[0] ?? null,
    [availableSessions, currentSessionId]
  );

  const resolvedSessionId = useMemo(() => {
    if (selectedSessionId && availableSessions.some((session) => session.session.id === selectedSessionId)) {
      return selectedSessionId;
    }

    if (!open) {
      return selectedSessionId;
    }

    return preferredSession?.session.id ?? "";
  }, [availableSessions, open, preferredSession, selectedSessionId]);

  const selectedSession = useMemo(
    () => availableSessions.find((session) => session.session.id === resolvedSessionId) ?? null,
    [availableSessions, resolvedSessionId]
  );

  useEffect(() => {
    if (!open || !resolvedSessionId) {
      return;
    }

    if (previewedSessionId === resolvedSessionId) {
      return;
    }

    let cancelled = false;

    void previewSource(resolvedSessionId, contextSource.source)
      .then((nextPreview) => {
        if (cancelled) {
          return;
        }

        setPreview(nextPreview);
        setPreviewedSessionId(resolvedSessionId);
      })
      .catch(() => {
        if (cancelled) {
          return;
        }

        setPreviewedSessionId(null);
      });

    return () => {
      cancelled = true;
    };
  }, [contextSource.source, open, previewSource, previewedSessionId, resolvedSessionId]);

  const error = createError ?? addError;
  const isBusy = isCreatingSession || isUpdatingSession;

  const resetState = useCallback((nextOpen: boolean) => {
    setOpen(nextOpen);

    if (!nextOpen) {
      setPreview(null);
      setPreviewedSessionId(null);
    }
  }, []);

  const handleStartFromSource = useCallback(async () => {
    if (!directory || isBusy) {
      setPickerOpen(true);
      return;
    }

    try {
      const { instanceId, session } = await createSession(directory, {
        title: contextSource.title,
      });

      await addSourceToSession(session.id, contextSource.source, true);

      resetState(false);
      refetch();
      navigate(`/sessions/${encodeURIComponent(session.id)}?instanceId=${encodeURIComponent(instanceId)}`);
    } catch {
      // handled by hook state
    }
  }, [addSourceToSession, contextSource.source, contextSource.title, createSession, directory, isBusy, navigate, refetch, resetState]);

  const handleAddToSession = useCallback(async () => {
    if (!resolvedSessionId || !preview || isBusy) {
      return;
    }

    try {
      await addSourceToSession(resolvedSessionId, contextSource.source, true);
      refetch();
      if (selectedSession) {
        navigate(buildSessionLocation(selectedSession));
      }
      resetState(false);
    } catch {
      // handled by hook state
    }
  }, [addSourceToSession, contextSource.source, isBusy, navigate, preview, refetch, resetState, selectedSession, resolvedSessionId]);

  const handleRetryPreview = useCallback(async () => {
    if (!resolvedSessionId || isBusy) {
      return;
    }

    try {
      const nextPreview = await previewSource(resolvedSessionId, contextSource.source);
      setPreview(nextPreview);
      setPreviewedSessionId(resolvedSessionId);
    } catch {
      setPreviewedSessionId(null);
    }
  }, [contextSource.source, isBusy, previewSource, resolvedSessionId]);

  return (
    <>
      <Button
        size="xs"
        variant="outline"
        className="gap-1"
        onClick={(event) => {
          event.stopPropagation();
          resetState(true);
        }}
      >
        <Rocket className="h-3 w-3" />
        Create Session
      </Button>

      <Dialog open={open} onOpenChange={resetState}>
        <DialogContent className="sm:max-w-xl top-[10%] translate-y-0">
          <DialogHeader>
            <DialogTitle>Create Session From Context</DialogTitle>
            <DialogDescription>
              Launch a new session through the shared source flow or preview this GitHub context before adding it to an existing session.
            </DialogDescription>
          </DialogHeader>

          <div className="mb-2">
            <Badge variant="secondary" className="gap-1 text-xs">
              {getContextLabel(contextSource)}
            </Badge>
            <p className="mt-1 truncate text-xs text-muted-foreground">{contextSource.url}</p>
          </div>

          <div className="space-y-4">
            <div className="space-y-2 rounded-md border px-3 py-3">
              <div>
                <p className="text-sm font-medium">Start a new session</p>
                <p className="text-xs text-muted-foreground">
                  Use the shared workspace source host. GitHub stays context-only; Fleet chooses the workspace source.
                </p>
              </div>

              <div className="flex flex-col gap-2 sm:flex-row">
                <Button onClick={() => setPickerOpen(true)} variant="outline" className="sm:flex-1" disabled={isBusy}>
                  <PlusSquare className="mr-2 h-4 w-4" />
                  Choose Workspace Source
                </Button>
                <Button
                  onClick={() => void handleStartFromSource()}
                  disabled={isBusy}
                  className="weave-gradient-bg hover:opacity-90 border-0 sm:flex-1"
                >
                  {isCreatingSession ? (
                    <>
                      <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                      Creating…
                    </>
                  ) : directory ? (
                    "Use Current Directory"
                  ) : (
                    "Quick Start Unavailable"
                  )}
                </Button>
              </div>
            </div>

            <div className="space-y-3 rounded-md border px-3 py-3">
              <div>
                <p className="text-sm font-medium">Add to existing session</p>
                <p className="text-xs text-muted-foreground">
                  Fleet previews backend-resolved content before it is added to a session.
                </p>
              </div>

              {availableSessions.length > 0 ? (
                <div className="space-y-3">
                  <div className="space-y-1.5">
                    <label className="text-sm font-medium" htmlFor="github-target-session">
                      Target session
                    </label>
                    <Select
                      value={resolvedSessionId}
                      onValueChange={(value) => {
                        setSelectedSessionId(value);
                        setPreview(null);
                        setPreviewedSessionId(null);
                      }}
                      disabled={isBusy}
                    >
                      <SelectTrigger id="github-target-session" className="w-full">
                        <SelectValue placeholder="Choose a session" />
                      </SelectTrigger>
                      <SelectContent>
                        {availableSessions.map((session) => (
                          <SelectItem key={session.session.id} value={session.session.id}>
                            {session.session.title}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </div>

                  <div className="space-y-2 rounded-md bg-muted/40 p-3">
                    <div className="flex items-center justify-between gap-2">
                      <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
                        Preview
                      </p>
                      {isUpdatingSession && !preview ? (
                        <span className="inline-flex items-center gap-1 text-xs text-muted-foreground">
                          <Loader2 className="h-3 w-3 animate-spin" />
                          Loading preview…
                        </span>
                      ) : null}
                    </div>

                    {preview ? (
                      <>
                        <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                          <Badge variant="outline">{preview.originLabel}</Badge>
                          <span>{preview.characterCount.toLocaleString()} chars</span>
                          {preview.isTruncated ? <span>Truncated before add</span> : null}
                        </div>
                        <pre className="max-h-56 overflow-auto whitespace-pre-wrap break-words rounded border bg-background p-3 text-xs">
                          {preview.content}
                        </pre>
                      </>
                    ) : (
                      <p className="text-xs text-muted-foreground">
                        {resolvedSessionId
                          ? "Preview will load before confirmation."
                          : "Choose a target session to preview this source."}
                      </p>
                    )}
                  </div>

                  <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
                    <Button variant="outline" onClick={() => void handleRetryPreview()} disabled={!resolvedSessionId || isBusy}>
                      Retry Preview
                    </Button>
                    <Button onClick={() => void handleAddToSession()} disabled={!preview || isBusy}>
                      {isUpdatingSession && preview ? (
                        <>
                          <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                          Adding…
                        </>
                      ) : (
                        "Add to Session"
                      )}
                    </Button>
                  </div>
                </div>
              ) : (
                <p className="text-xs text-muted-foreground">
                  No running sessions available. Start or resume a session first, then add this GitHub context.
                </p>
              )}
            </div>

            {error ? (
              <div className="flex items-start gap-2 rounded-md border border-red-500/20 bg-red-500/10 px-3 py-2 text-xs text-red-600 dark:text-red-400">
                <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
                <span>{error}</span>
              </div>
            ) : null}
          </div>
        </DialogContent>
      </Dialog>

      <NewSessionDialog
        open={pickerOpen}
        onOpenChange={setPickerOpen}
        defaultDirectory={directory}
        initialSource={contextSource.source}
        initialTitle={contextSource.title}
      />
    </>
  );
}
