// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";
import { PluginRuntimeProvider } from "@/plugins/context";
import { useSessionSources } from "@/session-sources/use-session-sources";

vi.mock("@/hooks/use-integrations", () => ({
  useIntegrations: () => ({
    pluginStatuses: [
      {
        pluginId: "github",
        status: "connected",
        actions: [],
      },
    ],
    isLoading: false,
    error: undefined,
    connect: vi.fn(),
    disconnect: vi.fn(),
    refetch: vi.fn(),
  }),
}));

describe("useSessionSources", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it("merges backend catalog with plugin session source contributions", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(
        JSON.stringify({
          sources: [
            {
              key: {
                providerId: "builtin.github",
                sourceType: "github-issue",
                actionId: "add-to-session",
                contractVersion: 1,
              },
              displayName: "GitHub issue",
              kind: "context",
              inputFields: [],
              producesWorkspace: false,
              producesContext: true,
              requiresConfirmation: true,
            },
          ],
        }),
        { status: 200 }
      )
    );

    const wrapper = ({ children }: { children: ReactNode }) => (
      <PluginRuntimeProvider>{children}</PluginRuntimeProvider>
    );

    const { result } = renderHook(() => useSessionSources(), { wrapper });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    expect(result.current.error).toBeNull();
    expect(result.current.sources).toHaveLength(1);
    expect(result.current.sources[0].descriptor.key.providerId).toBe("builtin.github");
    expect(result.current.sources[0].pluginId).toBe("github");
    expect(result.current.sources[0].label).toBe("GitHub Issue");
  });
});
