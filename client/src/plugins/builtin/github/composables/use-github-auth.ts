import { computed, onMounted, onUnmounted, readonly, shallowRef, watch, type ComputedRef, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";
import type { DeviceCodeResponse, PollRequest, PollResponse } from "@/lib/api-types";

export type GitHubDeviceFlowState =
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
  | { status: "expired" }
  | { status: "denied" }
  | { status: "error"; message: string };

export type GitHubPatState =
  | { status: "idle" }
  | { status: "testing" }
  | { status: "success"; username?: string }
  | { status: "error"; message: string };

interface GitHubStatusResponse {
  connected: boolean;
}

interface GitHubUserResponse {
  login: string;
}

export interface UseGitHubAuthOptions {
  autoLoadStatus?: boolean;
}

export interface UseGitHubAuthResult {
  deviceState: Readonly<ShallowRef<GitHubDeviceFlowState>>;
  isConnected: Readonly<ShallowRef<boolean>>;
  isLoadingStatus: Readonly<ShallowRef<boolean>>;
  isAwaitingAuthorization: Readonly<ComputedRef<boolean>>;
  personalAccessToken: ShallowRef<string>;
  patOpen: ShallowRef<boolean>;
  patState: Readonly<ShallowRef<GitHubPatState>>;
  loadStatus: () => Promise<void>;
  connectWithDeviceFlow: () => Promise<void>;
  resetDeviceFlow: () => void;
  copyUserCode: (code: string) => Promise<void>;
  testPersonalAccessToken: () => Promise<void>;
  connectWithPersonalAccessToken: () => Promise<void>;
  disconnectGitHub: () => Promise<void>;
}

const deviceState = shallowRef<GitHubDeviceFlowState>({ status: "idle" });
const isConnected = shallowRef(false);
const isLoadingStatus = shallowRef(false);
const personalAccessToken = shallowRef("");
const patOpen = shallowRef(false);
const patState = shallowRef<GitHubPatState>({ status: "idle" });

let pollTimeoutId: ReturnType<typeof setTimeout> | undefined;
let pollGeneration = 0;
let statusRequestId = 0;

function stopPolling(): void {
  pollGeneration += 1;

  if (!pollTimeoutId) {
    return;
  }

  clearTimeout(pollTimeoutId);
  pollTimeoutId = undefined;
}

async function readErrorMessage(response: Response, fallback: string): Promise<string> {
  const payload = (await response.json().catch(() => ({}))) as { error?: string; message?: string };
  return payload.error ?? payload.message ?? fallback;
}

async function verifyConnectedStatus(): Promise<boolean> {
  const response = await apiFetch("/api/integrations/github/auth/status");
  if (!response.ok) {
    throw new Error(await readErrorMessage(response, `HTTP ${response.status}`));
  }

  const payload = (await response.json()) as GitHubStatusResponse;
  isConnected.value = payload.connected;
  return payload.connected;
}

async function pollOnce(deviceCode: string, intervalSeconds: number, generation: number): Promise<void> {
  if (generation !== pollGeneration) {
    return;
  }

  try {
    const request: PollRequest = { deviceCode };
    const response = await apiFetch("/api/integrations/github/auth/poll", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request),
    });

    const payload = (await response.json().catch(() => ({}))) as PollResponse & { error?: string };
    if (!response.ok) {
      throw new Error(payload.error ?? payload.message ?? `HTTP ${response.status}`);
    }

    if (generation !== pollGeneration) {
      return;
    }

    if (payload.status === "complete") {
      const connected = await verifyConnectedStatus();
      deviceState.value = connected
        ? { status: "complete" }
        : {
            status: "error",
            message: "GitHub authorization completed, but the saved connection was not confirmed.",
          };
      stopPolling();
      return;
    }

    if (payload.status === "expired") {
      deviceState.value = { status: "expired" };
      stopPolling();
      return;
    }

    if (payload.status === "denied") {
      deviceState.value = { status: "denied" };
      stopPolling();
      return;
    }

    if (payload.status === "error") {
      deviceState.value = { status: "error", message: payload.message ?? "GitHub authorization failed." };
      stopPolling();
      return;
    }

    const nextInterval = payload.interval ?? intervalSeconds;
    pollTimeoutId = setTimeout(() => {
      void pollOnce(deviceCode, nextInterval, generation);
    }, nextInterval * 1000);
  } catch (error) {
    if (generation !== pollGeneration) {
      return;
    }

    deviceState.value = {
      status: "error",
      message: error instanceof Error ? error.message : "Failed while waiting for GitHub authorization.",
    };
    stopPolling();
  }
}

async function loadStatus(): Promise<void> {
  const requestId = ++statusRequestId;
  isLoadingStatus.value = true;

  try {
    await verifyConnectedStatus();
  } catch {
    if (requestId === statusRequestId) {
      isConnected.value = false;
    }
  } finally {
    if (requestId === statusRequestId) {
      isLoadingStatus.value = false;
    }
  }
}

async function connectWithDeviceFlow(): Promise<void> {
  stopPolling();
  deviceState.value = { status: "initiating" };

  try {
    const response = await apiFetch("/api/integrations/github/auth/device-code", { method: "POST" });
    if (!response.ok) {
      throw new Error(await readErrorMessage(response, "Failed to start GitHub authorization."));
    }

    const payload = (await response.json()) as DeviceCodeResponse;
    const expiresAt = Date.now() + payload.expiresIn * 1000;
    deviceState.value = {
      status: "awaiting-auth",
      userCode: payload.userCode,
      verificationUri: payload.verificationUri,
      deviceCode: payload.deviceCode,
      expiresAt,
      interval: payload.interval,
    };

    const generation = pollGeneration;
    pollTimeoutId = setTimeout(() => {
      void pollOnce(payload.deviceCode, payload.interval, generation);
    }, payload.interval * 1000);
  } catch (error) {
    deviceState.value = {
      status: "error",
      message: error instanceof Error ? error.message : "Failed to start GitHub authorization.",
    };
  }
}

function resetDeviceFlow(): void {
  stopPolling();
  deviceState.value = { status: "idle" };
}

async function copyUserCode(code: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(code);
  } catch {
    // Clipboard access unavailable.
  }
}

async function testPersonalAccessToken(): Promise<void> {
  const trimmedToken = personalAccessToken.value.trim();
  if (!trimmedToken) {
    patState.value = { status: "error", message: "Enter a personal access token." };
    return;
  }

  patState.value = { status: "testing" };

  try {
    const response = await fetch("https://api.github.com/user", {
      headers: {
        Authorization: `Bearer ${trimmedToken}`,
        Accept: "application/vnd.github+json",
      },
    });

    if (!response.ok) {
      throw new Error(await readErrorMessage(response, "Invalid GitHub token."));
    }

    const payload = (await response.json()) as GitHubUserResponse;
    patState.value = { status: "success", username: payload.login };
  } catch (error) {
    patState.value = {
      status: "error",
      message: error instanceof Error ? error.message : "Unable to validate GitHub token.",
    };
  }
}

async function connectWithPersonalAccessToken(): Promise<void> {
  const trimmedToken = personalAccessToken.value.trim();
  if (!trimmedToken) {
    patState.value = { status: "error", message: "Enter a personal access token." };
    return;
  }

  try {
    const response = await apiFetch("/api/integrations/github/auth/token", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ token: trimmedToken }),
    });

    if (!response.ok) {
      throw new Error(await readErrorMessage(response, "Failed to connect GitHub."));
    }

    personalAccessToken.value = "";
    patState.value = { status: "success" };
    isConnected.value = true;
    deviceState.value = { status: "complete" };
  } catch (error) {
    patState.value = {
      status: "error",
      message: error instanceof Error ? error.message : "Failed to connect GitHub.",
    };
  }
}

async function disconnectGitHub(): Promise<void> {
  try {
    const response = await apiFetch("/api/integrations/github/auth", { method: "DELETE" });
    if (!response.ok) {
      throw new Error(await readErrorMessage(response, `HTTP ${response.status}`));
    }

    stopPolling();
    isConnected.value = false;
    deviceState.value = { status: "idle" };
    patState.value = { status: "idle" };
    personalAccessToken.value = "";
  } catch (error) {
    deviceState.value = {
      status: "error",
      message: error instanceof Error ? error.message : "Failed to disconnect GitHub.",
    };
  }
}

export function useGitHubAuth(options: UseGitHubAuthOptions = {}): UseGitHubAuthResult {
  const { autoLoadStatus = true } = options;
  const isAwaitingAuthorization = computed(() => deviceState.value.status === "awaiting-auth");

  watch(personalAccessToken, () => {
    if (patState.value.status !== "idle") {
      patState.value = { status: "idle" };
    }
  });

  onMounted(() => {
    if (autoLoadStatus) {
      void loadStatus();
    }
  });

  onUnmounted(() => {
    stopPolling();
  });

  return {
    deviceState: readonly(deviceState),
    isConnected: readonly(isConnected),
    isLoadingStatus: readonly(isLoadingStatus),
    isAwaitingAuthorization,
    personalAccessToken,
    patOpen,
    patState: readonly(patState),
    loadStatus,
    connectWithDeviceFlow,
    resetDeviceFlow,
    copyUserCode,
    testPersonalAccessToken,
    connectWithPersonalAccessToken,
    disconnectGitHub,
  };
}
