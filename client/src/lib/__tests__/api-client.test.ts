import { describe, it, expect, beforeEach, vi } from "vitest";

// We need to test with different env var values, so we re-import the module
// after manipulating the env. Vitest's module cache must be reset between tests.

describe("api-client", () => {
  beforeEach(() => {
    vi.resetModules();
  });

  describe("when NEXT_PUBLIC_API_BASE_URL is unset", () => {
    beforeEach(() => {
      delete process.env.NEXT_PUBLIC_API_BASE_URL;
    });

    it("apiUrl returns the path unchanged", async () => {
      const { apiUrl } = await import("../api-client");
      expect(apiUrl("/api/sessions")).toBe("/api/sessions");
    });

    it("sseUrl returns the path unchanged", async () => {
      const { sseUrl } = await import("../api-client");
      expect(sseUrl("/api/notifications/stream")).toBe(
        "/api/notifications/stream"
      );
    });

    it("apiFetch calls fetch with the relative path", async () => {
      const mockFetch = vi.fn().mockResolvedValue(new Response("ok"));
      vi.stubGlobal("fetch", mockFetch);

      const { apiFetch } = await import("../api-client");
      await apiFetch("/api/sessions");

      expect(mockFetch).toHaveBeenCalledWith("/api/sessions", undefined);
      vi.unstubAllGlobals();
    });
  });

  describe("when NEXT_PUBLIC_API_BASE_URL is set", () => {
    beforeEach(() => {
      process.env.NEXT_PUBLIC_API_BASE_URL = "http://localhost:3000";
    });

    it("apiUrl prepends the base URL", async () => {
      const { apiUrl } = await import("../api-client");
      expect(apiUrl("/api/sessions")).toBe(
        "http://localhost:3000/api/sessions"
      );
    });

    it("sseUrl prepends the base URL", async () => {
      const { sseUrl } = await import("../api-client");
      expect(sseUrl("/api/notifications/stream")).toBe(
        "http://localhost:3000/api/notifications/stream"
      );
    });

    it("apiFetch calls fetch with the full URL", async () => {
      const mockFetch = vi.fn().mockResolvedValue(new Response("ok"));
      vi.stubGlobal("fetch", mockFetch);

      const { apiFetch } = await import("../api-client");
      await apiFetch("/api/sessions", { method: "POST" });

      expect(mockFetch).toHaveBeenCalledWith(
        "http://localhost:3000/api/sessions",
        { method: "POST" }
      );
      vi.unstubAllGlobals();
    });
  });

  describe("when NEXT_PUBLIC_API_BASE_URL has a trailing slash", () => {
    beforeEach(() => {
      process.env.NEXT_PUBLIC_API_BASE_URL = "http://localhost:3000/";
    });

    it("apiUrl strips the trailing slash before prepending", async () => {
      const { apiUrl } = await import("../api-client");
      expect(apiUrl("/api/sessions")).toBe(
        "http://localhost:3000/api/sessions"
      );
    });
  });
});
