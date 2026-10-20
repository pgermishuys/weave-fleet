import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises } from "@vue/test-utils";
import type { DomainEvent } from "@/lib/domain-events";

const { apiFetch, handlers } = vi.hoisted(() => ({ apiFetch: vi.fn(), handlers: [] as Array<(event: DomainEvent) => void> }));

vi.mock("@/lib/api-client", () => ({ apiFetch }));
vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: (_m: unknown, _t: string, handler: (event: DomainEvent) => void) => {
    handlers.push(handler);
    return () => undefined;
  },
  onReconnect: () => () => undefined,
}));
vi.mock("@/composables/use-weave-socket", () => ({ onReconnect: () => () => undefined }));

import { resetUsageLimitsForTests, useUsageLimits } from "@/composables/use-usage-limits";

const body = (name: string) => ({ enabled: true, providers: [{ provider: name, name, state: "ok", windows: [] }] });
const json = (value: unknown) => ({ ok: true, json: async () => value });

describe("useUsageLimits", () => {
  beforeEach(() => {
    resetUsageLimitsForTests();
    handlers.length = 0;
    apiFetch.mockReset();
    apiFetch.mockResolvedValue(json(body("claude")));
  });
  afterEach(() => vi.useRealTimers());

  it("loads once and follows the push", async () => {
    const { providers } = useUsageLimits();
    await flushPromises();
    expect(apiFetch).toHaveBeenCalledWith("/api/usage/limits");
    expect(providers.value.map((p) => p.provider)).toEqual(["claude"]);

    for (const handler of handlers) handler({ type: "usage.limits", payload: body("codex") } as unknown as DomainEvent);
    expect(providers.value.map((p) => p.provider)).toEqual(["codex"]);
  });

  it("asks with watch=true now and every minute while watched, and stops after", async () => {
    vi.useFakeTimers();
    const { watch } = useUsageLimits();
    await vi.advanceTimersByTimeAsync(0);
    apiFetch.mockClear();

    const stop = watch();
    await vi.advanceTimersByTimeAsync(0);
    expect(apiFetch).toHaveBeenCalledWith("/api/usage/limits?watch=true");
    await vi.advanceTimersByTimeAsync(60_000);
    expect(apiFetch).toHaveBeenCalledTimes(2);

    stop();
    await vi.advanceTimersByTimeAsync(120_000);
    expect(apiFetch).toHaveBeenCalledTimes(2);
  });

  it("allows and revokes a provider, then reloads", async () => {
    const { allow, revoke } = useUsageLimits();
    await flushPromises();
    apiFetch.mockClear();
    apiFetch.mockResolvedValue({ ...json(body("openrouter")), status: 204 });

    await allow("openrouter");
    expect(apiFetch).toHaveBeenNthCalledWith(1, "/api/usage/limits/openrouter/allow", { method: "POST" });
    expect(apiFetch).toHaveBeenNthCalledWith(2, "/api/usage/limits");
    await revoke("openrouter");
    expect(apiFetch).toHaveBeenCalledWith("/api/usage/limits/openrouter/allow", { method: "DELETE" });
  });

  it("throws when the server refuses", async () => {
    const { allow } = useUsageLimits();
    await flushPromises();
    apiFetch.mockResolvedValue({ ok: false, status: 403 });
    await expect(allow("codex")).rejects.toThrow("403");
  });
});
