import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api } from "@/api/client";
import { apiFetch, apiUrl, setApiBase, wsUrl } from "@/lib/api-client";
import {
  HOME_MACHINE_KEY,
  loadActiveMachineId,
  loadSessionMachines,
  machineRequestInit,
  normalizeBaseUrl,
  rememberSessionMachines,
  restoreActiveMachine,
  saveActiveMachineId,
  saveMachines,
  setActiveMachine,
  type MachineConnection,
} from "@/lib/machines";

const falcon: MachineConnection = {
  id: "f0a1c2d3e4f5a6b7c8d9e0f1a2b3c4d5",
  name: "falcon",
  baseUrl: "http://100.64.90.72:2113",
  token: "falcon-token_0123456789-abcdef",
  addedAt: "2026-09-26T00:00:00.000Z",
};

function lastFetch(fetchMock: ReturnType<typeof vi.fn>): { url: string; init: RequestInit; headers: Headers } {
  const [input, init = {}] = fetchMock.mock.calls.at(-1) as [RequestInfo | URL, RequestInit | undefined];
  const url = input instanceof Request ? input.url : String(input);
  const headers = new Headers(input instanceof Request ? input.headers : undefined);
  new Headers(init.headers).forEach((value, key) => headers.set(key, value));
  return { url, init, headers };
}

describe("machines", () => {
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    setActiveMachine(null);
    fetchMock = vi.fn(async () => new Response("[]", { status: 200, headers: { "Content-Type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    setActiveMachine(null);
    vi.unstubAllGlobals();
  });

  describe("requests", () => {
    it("sends the home machine's requests same-origin with its cookie", async () => {
      await apiFetch("/api/sessions");

      const { url, init, headers } = lastFetch(fetchMock);
      expect(url).toBe("/api/sessions");
      expect(init.credentials).toBe("include");
      expect(headers.has("Authorization")).toBe(false);
    });

    it("sends another machine's requests to it with its token and no cookies", async () => {
      setActiveMachine(falcon);

      await apiFetch("/api/sessions", { method: "POST", body: "{}" });

      const { url, init, headers } = lastFetch(fetchMock);
      expect(url).toBe("http://100.64.90.72:2113/api/sessions");
      expect(init.credentials).toBe("omit");
      expect(headers.get("Authorization")).toBe(`Bearer ${falcon.token}`);
      expect(headers.has("X-CSRF-Token")).toBe(false);
    });

    it("sends the typed client's requests to the live machine, decided per call", async () => {
      await api.GET("/api/sessions");
      expect(lastFetch(fetchMock).url).toMatch(/^http:\/\/localhost(:\d+)?\/api\/sessions$/);

      setActiveMachine(falcon);
      await api.GET("/api/sessions");

      const { url, init, headers } = lastFetch(fetchMock);
      expect(url).toBe("http://100.64.90.72:2113/api/sessions");
      expect(init.credentials).toBe("omit");
      expect(headers.get("Authorization")).toBe(`Bearer ${falcon.token}`);
    });

    it("lets setApiBase reach the typed client, which captured it once before", async () => {
      setApiBase("http://kestrel.example:2113");
      try {
        await api.GET("/api/sessions");
        expect(lastFetch(fetchMock).url).toBe("http://kestrel.example:2113/api/sessions");
      } finally {
        setApiBase("");
      }
    });

    it("keeps a request's body when it goes to another machine", async () => {
      setActiveMachine(falcon);

      await api.PATCH("/api/sessions/{id}", {
        params: { path: { id: "s1" } },
        body: { title: "Renamed" } as never,
      });

      const [input] = fetchMock.mock.calls.at(-1) as [Request];
      expect(input.method).toBe("PATCH");
      expect(await input.text()).toBe(JSON.stringify({ title: "Renamed" }));
    });

    it("puts the token in a socket's query, since a browser socket can't send headers", () => {
      setActiveMachine(falcon);

      expect(wsUrl("/api/sessions/s1/terminals/t1/socket?cols=80")).toBe(
        `ws://100.64.90.72:2113/api/sessions/s1/terminals/t1/socket?cols=80&access_token=${encodeURIComponent(falcon.token)}`,
      );
      expect(apiUrl("/hubs/session-events")).toBe("http://100.64.90.72:2113/hubs/session-events");
    });

    it("drops a CSRF header and cookies for another machine even when the caller set them", () => {
      const init = machineRequestInit(falcon, { credentials: "include", headers: { "X-CSRF-Token": "abc" } });

      expect(init.credentials).toBe("omit");
      expect(new Headers(init.headers).has("X-CSRF-Token")).toBe(false);
    });
  });

  describe("urls", () => {
    it.each([
      ["100.64.90.72:2113", "http://100.64.90.72:2113"],
      ["http://falcon.tail9c2e.ts.net:2113/", "http://falcon.tail9c2e.ts.net:2113"],
      ["https://falcon.tail9c2e.ts.net", "https://falcon.tail9c2e.ts.net"],
      ["  https://falcon.tail9c2e.ts.net/fleet//  ", "https://falcon.tail9c2e.ts.net/fleet"],
    ])("normalizes %s", (input, expected) => {
      expect(normalizeBaseUrl(input)).toBe(expected);
    });
  });

  describe("restoring the live machine", () => {
    beforeEach(() => {
      saveMachines([falcon]);
    });

    it("starts at home in a fresh tab", () => {
      expect(restoreActiveMachine("/")).toBeNull();
    });

    it("stays on the machine this tab was working in", () => {
      saveActiveMachineId(falcon.id);

      expect(restoreActiveMachine("/settings")?.id).toBe(falcon.id);
    });

    it("opens a session on the machine it lives on, whichever machine the tab was on", () => {
      rememberSessionMachines(falcon.id, ["remote-session"]);
      rememberSessionMachines(null, ["home-session"]);

      saveActiveMachineId(null);
      expect(restoreActiveMachine("/sessions/remote-session")?.id).toBe(falcon.id);
      expect(loadActiveMachineId()).toBe(falcon.id);

      expect(restoreActiveMachine("/sessions/home-session")).toBeNull();
      expect(loadActiveMachineId()).toBeNull();
    });

    it("goes home when the machine the tab was on has been forgotten", () => {
      saveActiveMachineId("gone");

      expect(restoreActiveMachine("/")).toBeNull();
      expect(loadActiveMachineId()).toBeNull();
    });

    it("keeps sessions a machine stops listing, so archived ones still open on it", () => {
      rememberSessionMachines(falcon.id, ["a", "b"]);
      rememberSessionMachines(null, ["c"]);

      rememberSessionMachines(falcon.id, ["b"]);

      expect(loadSessionMachines()).toEqual({ a: falcon.id, b: falcon.id, c: HOME_MACHINE_KEY });
    });
  });
});
