"use client";

import { useEffect, useState, useCallback } from "react";
import { Download, RefreshCw, AlertCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Progress } from "@/components/ui/progress";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
} from "@/components/ui/dialog";
import {
  isTauri,
  tauriGetUpdateState,
  tauriListen,
  tauriInvoke,
  tauriSetUpdatePreferences,
} from "@/lib/tauri";
import { useUpdatePreferences } from "@/lib/update-preferences";

interface UpdateAvailablePayload {
  version: string;
  current_version: string;
}

interface UpdateProgressPayload {
  downloaded: number;
  total: number | null;
}

type UpdateState = "idle" | "available" | "downloading" | "ready" | "error";

function showUpdate(
  payload: UpdateAvailablePayload,
  setVersion: (v: string) => void,
  setCurrentVersion: (v: string) => void,
  setState: (s: UpdateState) => void,
  setOpen: (o: boolean) => void,
) {
  setVersion(payload.version);
  setCurrentVersion(payload.current_version);
  setState("available");
  setOpen(true);
}

export function TauriUpdateDialog() {
  const [updatePreferences] = useUpdatePreferences();
  const [state, setState] = useState<UpdateState>("idle");
  const [version, setVersion] = useState("");
  const [currentVersion, setCurrentVersion] = useState("");
  const [progress, setProgress] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  useEffect(() => {
    if (!isTauri()) return;

    tauriSetUpdatePreferences(
      updatePreferences.autoUpdate,
      updatePreferences.channel,
    ).catch(() => {
      // Command unavailable or not ready; ignore and keep client preference.
    });
  }, [updatePreferences]);

  // Listen for the Rust-emitted "update-available" event AND poll for any
  // pending update that fired before the listener was registered.
  useEffect(() => {
    if (!isTauri()) return;

    tauriGetUpdateState()
      .then((updateState) => {
        if (updateState.update_available) {
          setVersion(updateState.update_available.version);
          setCurrentVersion(updateState.update_available.current_version);
          setState(updateState.download_in_progress ? "downloading" : "available");
          if (!updatePreferences.autoUpdate) {
            setOpen(true);
          }
        }

        if (updateState.update_ready_for_restart) {
          setState("ready");
          setOpen(true);
        }
      })
      .catch(() => {
        // Ignore when command is unavailable.
      });

    let cancelled = false;
    let unlisten: (() => void) | null = null;

    // Register event listener
    tauriListen<UpdateAvailablePayload>("update-available", (payload) => {
      if (!cancelled) {
        if (!updatePreferences.autoUpdate) {
          showUpdate(payload, setVersion, setCurrentVersion, setState, setOpen);
        } else {
          setVersion(payload.version);
          setCurrentVersion(payload.current_version);
          setState("downloading");
        }
      }
    }).then((fn) => {
      if (cancelled) {
        // Component unmounted before listener was registered — clean up immediately
        fn?.();
      } else {
        unlisten = fn;
      }
    });

    // Pull-based fallback: check for any pending update stored in Rust state.
    // This handles the race where the Rust event fires before the JS listener
    // is registered.
    tauriInvoke<UpdateAvailablePayload | null>("check_for_update")
      .then((payload) => {
        if (payload && !cancelled && !updatePreferences.autoUpdate) {
            showUpdate(payload, setVersion, setCurrentVersion, setState, setOpen);
        }
      })
      .catch(() => {
        // Not in Tauri or command not available — ignore
      });

    return () => {
      cancelled = true;
      unlisten?.();
    };
  }, [updatePreferences.autoUpdate]);

  useEffect(() => {
    if (!isTauri()) return;

    let cancelled = false;
    let unlisten: (() => void) | null = null;

    tauriListen<UpdateAvailablePayload>("update-ready-for-restart", (payload) => {
      if (!cancelled) {
        setVersion(payload.version);
        setCurrentVersion(payload.current_version);
        setState("ready");
        setOpen(true);
      }
    }).then((fn) => {
      if (cancelled) {
        fn?.();
      } else {
        unlisten = fn;
      }
    });

    return () => {
      cancelled = true;
      unlisten?.();
    };
  }, []);

  // Listen for download progress events
  useEffect(() => {
    if (!isTauri()) return;

    let cancelled = false;
    let unlisten: (() => void) | null = null;

    tauriListen<UpdateProgressPayload>(
      "update-download-progress",
      (payload) => {
        if (!cancelled && payload.total && payload.total > 0) {
          setProgress(Math.round((payload.downloaded / payload.total) * 100));
        }
      },
    ).then((fn) => {
      if (cancelled) {
        fn?.();
      } else {
        unlisten = fn;
      }
    });

    return () => {
      cancelled = true;
      unlisten?.();
    };
  }, []);

  const handleInstall = useCallback(async () => {
    if (!isTauri()) return;

    setState("downloading");
    setProgress(0);
    setError(null);

    try {
      await tauriInvoke("install_update");
      // If we get here, the app will restart — this line is unlikely to run
    } catch (e) {
      const message = e instanceof Error ? e.message : String(e);
      setError(message);
      setState("error");
    }
  }, []);

  const handleDismiss = useCallback(() => {
    if (state !== "downloading") {
      setOpen(false);
      // Reset state so the dialog can re-open on next check
      setState("idle");
    }
  }, [state]);

  // Don't render anything outside Tauri or when there's no update
  if (!isTauri() || state === "idle") return null;

  return (
    <Dialog open={open} onOpenChange={handleDismiss}>
      <DialogContent
        className="sm:max-w-md"
        showCloseButton={state !== "downloading"}
      >
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            {state === "error" ? (
              <AlertCircle className="h-5 w-5 text-red-500" />
            ) : (
              <Download className="h-5 w-5" />
            )}
            {state === "error"
              ? "Update Failed"
              : state === "ready"
                ? "Update Ready"
                : "Update Available"}
          </DialogTitle>
          <DialogDescription>
            {state === "error"
              ? "Something went wrong while installing the update."
              : state === "ready"
                ? `Weave Fleet v${version} has been downloaded and will be applied on next start.`
              : `Weave Fleet v${version} is available (you have v${currentVersion}).`}
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4 py-2">
          {state === "downloading" && (
            <div className="space-y-2">
              <div className="flex items-center justify-between text-sm text-muted-foreground">
                <span className="flex items-center gap-1.5">
                  <RefreshCw className="h-3.5 w-3.5 animate-spin" />
                  Downloading update...
                </span>
                <span>{progress}%</span>
              </div>
              <Progress value={progress} />
              <p className="text-xs text-muted-foreground">
                {updatePreferences.autoUpdate
                  ? "The update is downloading in the background."
                  : "The app will restart automatically once the update is installed."}
              </p>
            </div>
          )}

          {state === "ready" && (
            <div className="rounded-md bg-emerald-500/10 p-3 text-sm text-emerald-700 dark:text-emerald-400">
              Update downloaded. Restart the app to switch to v{version}.
            </div>
          )}

          {state === "error" && error && (
            <div className="rounded-md bg-red-500/10 p-3 text-sm text-red-600 dark:text-red-400">
              {error}
            </div>
          )}
        </div>

        <DialogFooter>
          {state === "available" && (
            <>
              <Button variant="outline" onClick={handleDismiss}>
                Not Now
              </Button>
              <Button onClick={handleInstall} className="gap-1.5">
                <Download className="h-3.5 w-3.5" />
                Install &amp; Restart
              </Button>
            </>
          )}
          {state === "error" && (
            <>
              <Button variant="outline" onClick={handleDismiss}>
                Dismiss
              </Button>
              <Button onClick={handleInstall} className="gap-1.5">
                <RefreshCw className="h-3.5 w-3.5" />
                Retry
              </Button>
            </>
          )}
          {state === "ready" && (
            <Button variant="outline" onClick={handleDismiss}>
              Got it
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
