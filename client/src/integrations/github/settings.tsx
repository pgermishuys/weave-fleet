
import { useState, useEffect, useRef, useCallback } from "react";
import { CheckCircle, AlertCircle, Loader2, Copy, ExternalLink, ChevronDown } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Collapsible,
  CollapsibleTrigger,
  CollapsibleContent,
} from "@/components/ui/collapsible";
import { useIntegrationsContext } from "@/contexts/integrations-context";
import { apiFetch } from "@/lib/api-client";
import type { DeviceCodeResponse, PollResponse } from "@/lib/api-types";

// ─── Device flow state machine ────────────────────────────────────────────────

type DeviceFlowState =
  | { status: "idle" }
  | { status: "initiating" }
  | {
      status: "awaiting-auth";
      userCode: string;
      verificationUri: string;
      deviceCode: string;
      expiresAt: number;
      interval: number;
    }
  | { status: "complete" }
  | { status: "error"; message: string }
  | { status: "expired" }
  | { status: "denied" };

// ─── PAT connection state ──────────────────────────────────────────────────────

type PatTestState =
  | { status: "idle" }
  | { status: "testing" }
  | { status: "success"; username: string }
  | { status: "error"; message: string };

interface GitHubUserResponse {
  login: string;
}

interface GitHubStatusResponse {
  connected: boolean;
}

// ─── Component ────────────────────────────────────────────────────────────────

/**
 * GitHub settings component.
 *
 * Primary: GitHub Device Authorization flow (RFC 8628) — user clicks
 * "Connect with GitHub", a code and URL are shown, and the connection
 * completes automatically once the user authorizes in their browser.
 *
 * Secondary: Personal Access Token entry in a collapsible "Advanced" section.
 */
export function GitHubSettings() {
  const { connect, integrations, refetch } = useIntegrationsContext();

  // ── Device flow state ──
  const [deviceState, setDeviceState] = useState<DeviceFlowState>({ status: "idle" });
  const pollTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const isMountedRef = useRef(true);

  // ── PAT state ──
  const [token, setToken] = useState("");
  const [patTestState, setPatTestState] = useState<PatTestState>({ status: "idle" });
  const [isConnecting, setIsConnecting] = useState(false);
  const [patOpen, setPatOpen] = useState(false);

  const isConnected = integrations.some(
    (i) => i.id === "github" && i.status === "connected"
  );

  useEffect(() => {
    if (isConnected && deviceState.status === "complete") {
      setDeviceState({ status: "idle" });
    }
  }, [deviceState.status, isConnected]);

  // ── Cleanup on unmount ──
  useEffect(() => {
    isMountedRef.current = true;
    return () => {
      isMountedRef.current = false;
      if (pollTimerRef.current) clearTimeout(pollTimerRef.current);
    };
  }, []);

  // ── Stop polling helper ──
  const stopPolling = useCallback(() => {
    if (pollTimerRef.current) {
      clearTimeout(pollTimerRef.current);
      pollTimerRef.current = null;
    }
  }, []);

  // ── Poll once for a token ──
  const pollOnce = useCallback(
    async (deviceCode: string, intervalSecs: number) => {
      if (!isMountedRef.current) return;

      let result: PollResponse;
      try {
        const res = await apiFetch("/api/integrations/github/auth/poll", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ deviceCode }),
        });
        result = (await res.json()) as PollResponse;
      } catch {
        if (!isMountedRef.current) return;
        setDeviceState({ status: "error", message: "Network error while polling" });
        return;
      }

      if (!isMountedRef.current) return;

      if (result.status === "complete") {
        try {
          const statusResponse = await apiFetch("/api/integrations/github/auth/status");
          const status = (await statusResponse.json()) as GitHubStatusResponse;

          if (!isMountedRef.current) return;

          if (!status.connected) {
            setDeviceState({
              status: "error",
              message: "GitHub authorization completed, but the saved connection was not confirmed. Refresh and try again.",
            });
            return;
          }

          setDeviceState({ status: "complete" });
          void refetch();
        } catch {
          if (!isMountedRef.current) return;
          setDeviceState({
            status: "error",
            message: "GitHub authorization completed, but connection status could not be verified.",
          });
        }

        return;
      }

      if (result.status === "expired") {
        setDeviceState({ status: "expired" });
        return;
      }

      if (result.status === "denied") {
        setDeviceState({ status: "denied" });
        return;
      }

      if (result.status === "error") {
        setDeviceState({ status: "error", message: result.message ?? "Unknown error" });
        return;
      }

      // Still pending — schedule next poll, respecting updated interval on slow_down
      const nextInterval = result.interval ?? intervalSecs;
      pollTimerRef.current = setTimeout(
        () => pollOnce(deviceCode, nextInterval),
        nextInterval * 1000
      );
    },
    [refetch]
  );

  // ── Initiate device flow ──
  async function handleConnectWithGitHub() {
    setDeviceState({ status: "initiating" });
    stopPolling();

    let data: DeviceCodeResponse;
    try {
      const res = await apiFetch("/api/integrations/github/auth/device-code", {
        method: "POST",
      });
      if (!res.ok) {
        const err = (await res.json().catch(() => ({}))) as { error?: string };
        setDeviceState({
          status: "error",
          message: err.error ?? "Failed to start device authorization",
        });
        return;
      }
      data = (await res.json()) as DeviceCodeResponse;
    } catch {
      setDeviceState({ status: "error", message: "Network error" });
      return;
    }

    const expiresAt = Date.now() + data.expiresIn * 1000;
    setDeviceState({
      status: "awaiting-auth",
      userCode: data.userCode,
      verificationUri: data.verificationUri,
      deviceCode: data.deviceCode,
      expiresAt,
      interval: data.interval,
    });

    // Start polling after first interval
    pollTimerRef.current = setTimeout(
      () => pollOnce(data.deviceCode, data.interval),
      data.interval * 1000
    );
  }

  // ── Cancel device flow ──
  function handleCancel() {
    stopPolling();
    setDeviceState({ status: "idle" });
  }

  // ── Reset to idle ──
  function handleTryAgain() {
    stopPolling();
    setDeviceState({ status: "idle" });
  }

  // ── Copy code to clipboard ──
  async function handleCopyCode(code: string) {
    try {
      await navigator.clipboard.writeText(code);
    } catch {
      // silently ignore clipboard errors
    }
  }

  // ── PAT: test connection ──
  async function handleTestConnection() {
    if (!token.trim()) return;
    setPatTestState({ status: "testing" });

    try {
      const userResponse = await fetch("https://api.github.com/user", {
        headers: {
          Authorization: `Bearer ${token}`,
          Accept: "application/vnd.github+json",
        },
      });

      if (userResponse.ok) {
        const user = (await userResponse.json()) as GitHubUserResponse;
        setPatTestState({ status: "success", username: user.login });
      } else {
        const data = (await userResponse.json().catch(() => ({}))) as { message?: string };
        setPatTestState({
          status: "error",
          message: data.message ?? "Invalid token",
        });
      }
    } catch {
      setPatTestState({ status: "error", message: "Network error" });
    }
  }

  // ── PAT: connect ──
  async function handlePatConnect() {
    if (!token.trim()) return;
    setIsConnecting(true);
    try {
      await connect("github", { token: token.trim() });
      setToken("");
      setPatTestState({ status: "idle" });
    } catch (err) {
      setPatTestState({
        status: "error",
        message: err instanceof Error ? err.message : "Failed to connect",
      });
    } finally {
      setIsConnecting(false);
    }
  }

  if (isConnected) {
    return null; // Parent (integrations-tab) handles the disconnect button
  }

  // ── Render ────────────────────────────────────────────────────────────────────

  return (
    <div className="space-y-4">
      {/* ── Device flow section ── */}
      <div className="space-y-3">
        {deviceState.status === "idle" && (
          <Button
            size="sm"
            className="w-full"
            onClick={handleConnectWithGitHub}
          >
            Connect with GitHub
          </Button>
        )}

        {deviceState.status === "initiating" && (
          <Button size="sm" className="w-full" disabled>
            <Loader2 className="h-3.5 w-3.5 mr-1.5 animate-spin" />
            Starting authorization…
          </Button>
        )}

        {deviceState.status === "awaiting-auth" && (
          <div className="space-y-3 rounded-md border p-3 text-sm">
            <p className="text-muted-foreground">
              Open the link below and enter the code to authorize Weave Agent Fleet:
            </p>

            {/* Verification URL */}
            <a
              href={deviceState.verificationUri}
              target="_blank"
              rel="noopener noreferrer"
              className="flex items-center gap-1.5 text-primary hover:underline font-medium"
            >
              <ExternalLink className="h-3.5 w-3.5 shrink-0" />
              {deviceState.verificationUri}
            </a>

            {/* User code */}
            <div className="flex items-center gap-2">
              <span className="font-mono text-xl font-bold tracking-widest">
                {deviceState.userCode}
              </span>
              <Button
                size="icon"
                variant="ghost"
                className="h-7 w-7"
                onClick={() => handleCopyCode(deviceState.userCode)}
                title="Copy code"
              >
                <Copy className="h-3.5 w-3.5" />
              </Button>
            </div>

            {/* Waiting indicator */}
            <div className="flex items-center gap-1.5 text-muted-foreground">
              <Loader2 className="h-3.5 w-3.5 animate-spin" />
              Waiting for authorization…
            </div>

            <Button
              size="sm"
              variant="outline"
              onClick={handleCancel}
            >
              Cancel
            </Button>
          </div>
        )}

        {deviceState.status === "complete" && (
          <div className="flex items-center gap-1.5 text-xs text-green-600 dark:text-green-400">
            <CheckCircle className="h-3.5 w-3.5" />
            Connected successfully
          </div>
        )}

        {deviceState.status === "error" && (
          <div className="space-y-2">
            <div className="flex items-center gap-1.5 text-xs text-destructive">
              <AlertCircle className="h-3.5 w-3.5" />
              {deviceState.message}
            </div>
            <Button size="sm" variant="outline" onClick={handleTryAgain}>
              Try Again
            </Button>
          </div>
        )}

        {deviceState.status === "expired" && (
          <div className="space-y-2">
            <div className="flex items-center gap-1.5 text-xs text-destructive">
              <AlertCircle className="h-3.5 w-3.5" />
              Code expired. Please try again.
            </div>
            <Button size="sm" variant="outline" onClick={handleTryAgain}>
              Try Again
            </Button>
          </div>
        )}

        {deviceState.status === "denied" && (
          <div className="space-y-2">
            <div className="flex items-center gap-1.5 text-xs text-destructive">
              <AlertCircle className="h-3.5 w-3.5" />
              Authorization was denied.
            </div>
            <Button size="sm" variant="outline" onClick={handleTryAgain}>
              Try Again
            </Button>
          </div>
        )}
      </div>

      {/* ── PAT section (collapsible advanced) ── */}
      <Collapsible open={patOpen} onOpenChange={setPatOpen}>
        <CollapsibleTrigger asChild>
          <button
            type="button"
            className="flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground transition-colors"
          >
            <ChevronDown
              className={`h-3 w-3 transition-transform ${patOpen ? "rotate-180" : ""}`}
            />
            Advanced: Use Personal Access Token
          </button>
        </CollapsibleTrigger>

        <CollapsibleContent className="pt-2 space-y-2">
          <Input
            type="password"
            placeholder="ghp_xxxxxxxxxxxx"
            value={token}
            onChange={(e) => setToken(e.target.value)}
            disabled={isConnecting}
          />

          {patTestState.status === "success" && (
            <div className="flex items-center gap-1.5 text-xs text-green-600 dark:text-green-400">
              <CheckCircle className="h-3.5 w-3.5" />
              Connected as @{patTestState.username}
            </div>
          )}

          {patTestState.status === "error" && (
            <div className="flex items-center gap-1.5 text-xs text-destructive">
              <AlertCircle className="h-3.5 w-3.5" />
              {patTestState.message}
            </div>
          )}

          <div className="flex items-center gap-2">
            <Button
              size="sm"
              variant="outline"
              onClick={handleTestConnection}
              disabled={!token.trim() || patTestState.status === "testing" || isConnecting}
            >
              {patTestState.status === "testing" && (
                <Loader2 className="h-3.5 w-3.5 mr-1.5 animate-spin" />
              )}
              Test Connection
            </Button>

            <Button
              size="sm"
              onClick={handlePatConnect}
              disabled={!token.trim() || isConnecting}
            >
              {isConnecting && (
                <Loader2 className="h-3.5 w-3.5 mr-1.5 animate-spin" />
              )}
              Connect
            </Button>
          </div>
        </CollapsibleContent>
      </Collapsible>
    </div>
  );
}
