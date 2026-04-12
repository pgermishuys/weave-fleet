import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { renderHook, act } from "@testing-library/react";

describe("useSendPrompt", () => {
  beforeEach(() => {
    vi.resetModules();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
  });

  it("posts slash command arguments using the arguments field", async () => {
    const mockFetch = vi.fn().mockResolvedValue(new Response(null, { status: 202 }));
    vi.stubGlobal("fetch", mockFetch);

    const { useSendPrompt } = await import("../use-send-prompt");
    const { result } = renderHook(() => useSendPrompt());

    await act(async () => {
      await result.current.sendPrompt(
        "session-1",
        "instance-1",
        "/compact foo bar",
        "loom",
        { providerID: "openai", modelID: "gpt-5" }
      );
    });

    expect(mockFetch).toHaveBeenCalledTimes(1);
    const [url, init] = mockFetch.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/sessions/session-1/command");
    expect(init.method).toBe("POST");

    const body = JSON.parse(String(init.body));
    expect(body).toEqual({
      instanceId: "instance-1",
      command: "compact",
      arguments: "foo bar",
      agent: "loom",
      model: "openai/gpt-5",
    });
    expect(body).not.toHaveProperty("args");
  });

  it("omits arguments when slash command has no parameters", async () => {
    const mockFetch = vi.fn().mockResolvedValue(new Response(null, { status: 202 }));
    vi.stubGlobal("fetch", mockFetch);

    const { useSendPrompt } = await import("../use-send-prompt");
    const { result } = renderHook(() => useSendPrompt());

    await act(async () => {
      await result.current.sendPrompt("session-2", "instance-2", "/metrics");
    });

    expect(mockFetch).toHaveBeenCalledTimes(1);
    const [, init] = mockFetch.mock.calls[0] as [string, RequestInit];
    const body = JSON.parse(String(init.body));

    expect(body).toEqual({
      instanceId: "instance-2",
      command: "metrics",
    });
    expect(body).not.toHaveProperty("arguments");
    expect(body).not.toHaveProperty("args");
  });

  it("sends regular prompt model payload as provider/model string", async () => {
    const mockFetch = vi.fn().mockResolvedValue(new Response(null, { status: 202 }));
    vi.stubGlobal("fetch", mockFetch);

    const { useSendPrompt } = await import("../use-send-prompt");
    const { result } = renderHook(() => useSendPrompt());

    await act(async () => {
      await result.current.sendPrompt(
        "session-3",
        "instance-3",
        "Explain this diff",
        "loom",
        { providerID: "openai", modelID: "gpt-5" }
      );
    });

    expect(mockFetch).toHaveBeenCalledTimes(1);
    const [url, init] = mockFetch.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/sessions/session-3/prompt");

    const body = JSON.parse(String(init.body));
    expect(body).toEqual({
      instanceId: "instance-3",
      text: "Explain this diff",
      agent: "loom",
      model: "openai/gpt-5",
    });
  });
});
