import { defineStore } from "pinia";
import { shallowRef } from "vue";
import {
  api,
  type HarnessSignInAttempt,
  type HarnessSignInAttemptStatus,
  type HarnessSignIns,
} from "@/api/client";
import { forgetHarnessCatalogs } from "@/composables/use-harness-catalog";

/** An answer to a sign-in method's field. */
export type SignInAnswer = string | number | boolean | string[];

/**
 * A harness's provider sign-ins, by harness, and what changes them. A key or code goes into one request body and
 * isn't kept here. After a sign-in, `changed` lists them again and makes the composers ask for their models again
 * (switching and signing out do that themselves).
 */
export const useHarnessSignInStore = defineStore("harness-sign-in", () => {
  const byHarness = shallowRef<Readonly<Record<string, HarnessSignIns>>>({});

  function signInsFor(harnessType: string): HarnessSignIns | null {
    return byHarness.value[harnessType] ?? null;
  }

  async function load(harnessType: string): Promise<void> {
    const { data, error, response } = await api.GET("/api/harnesses/{harnessType}/sign-in", {
      params: { path: { harnessType } },
    });
    if (!response.ok || !data) throw new Error(errorMessage(error, response));
    byHarness.value = { ...byHarness.value, [harnessType]: data as unknown as HarnessSignIns };
  }

  /** After a sign-in changed: the models on offer changed too, and the list shows the new sign-ins. */
  async function changed(harnessType: string): Promise<void> {
    forgetHarnessCatalogs(harnessType);
    await load(harnessType);
  }

  async function signInWithKey(
    harnessType: string,
    providerId: string,
    key: string,
    answers: Record<string, SignInAnswer>,
  ): Promise<void> {
    const { error, response } = await api.POST("/api/harnesses/{harnessType}/sign-in/{providerId}/key", {
      params: { path: { harnessType, providerId } },
      body: { key, answers: answers as never },
    });
    if (!response.ok) throw new Error(errorMessage(error, response));
  }

  async function start(
    harnessType: string,
    providerId: string,
    methodId: string,
    answers: Record<string, SignInAnswer>,
  ): Promise<HarnessSignInAttempt> {
    const { data, error, response } = await api.POST("/api/harnesses/{harnessType}/sign-in/{providerId}/attempts", {
      params: { path: { harnessType, providerId } },
      body: { methodId, answers: answers as never },
    });
    if (!response.ok || !data) throw new Error(errorMessage(error, response));
    return data as unknown as HarnessSignInAttempt;
  }

  async function status(harnessType: string, providerId: string, attemptId: string): Promise<HarnessSignInAttemptStatus> {
    const { data, error, response } = await api.GET("/api/harnesses/{harnessType}/sign-in/{providerId}/attempts/{attemptId}", {
      params: { path: { harnessType, providerId, attemptId } },
    });
    if (!response.ok || !data) throw new Error(errorMessage(error, response));
    return data as unknown as HarnessSignInAttemptStatus;
  }

  async function submitCode(harnessType: string, providerId: string, attemptId: string, code: string): Promise<void> {
    const { error, response } = await api.POST("/api/harnesses/{harnessType}/sign-in/{providerId}/attempts/{attemptId}/code", {
      params: { path: { harnessType, providerId, attemptId } },
      body: { code },
    });
    if (!response.ok) throw new Error(errorMessage(error, response));
  }

  /** The address a browser on another device landed on after the provider's page, for the listener on Fleet's computer. */
  async function forwardCallback(harnessType: string, providerId: string, attemptId: string, address: string): Promise<void> {
    const { error, response } = await api.POST("/api/harnesses/{harnessType}/sign-in/{providerId}/attempts/{attemptId}/callback", {
      params: { path: { harnessType, providerId, attemptId } },
      body: { address },
    });
    if (!response.ok) throw new Error(errorMessage(error, response));
  }

  async function cancel(harnessType: string, providerId: string, attemptId: string): Promise<void> {
    await api.DELETE("/api/harnesses/{harnessType}/sign-in/{providerId}/attempts/{attemptId}", {
      params: { path: { harnessType, providerId, attemptId } },
    });
  }

  async function use(harnessType: string, connectionId: string): Promise<void> {
    const { error, response } = await api.POST("/api/harnesses/{harnessType}/sign-in/connections/{connectionId}/use", {
      params: { path: { harnessType, connectionId } },
    });
    if (!response.ok) throw new Error(errorMessage(error, response));
    await changed(harnessType);
  }

  async function signOut(harnessType: string, connectionId: string): Promise<void> {
    const { error, response } = await api.DELETE("/api/harnesses/{harnessType}/sign-in/connections/{connectionId}", {
      params: { path: { harnessType, connectionId } },
    });
    if (!response.ok) throw new Error(errorMessage(error, response));
    await changed(harnessType);
  }

  return { byHarness, signInsFor, load, changed, signInWithKey, start, status, submitCode, forwardCallback, cancel, use, signOut };
});

function errorMessage(error: unknown, response: Response): string {
  const payload = error as { error?: string; detail?: string; title?: string } | undefined;
  return payload?.error ?? payload?.detail ?? `Fleet couldn't reach the harness (HTTP ${response.status}).`;
}
