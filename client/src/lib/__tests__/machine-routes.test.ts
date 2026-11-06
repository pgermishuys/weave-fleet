import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  _resetForTesting,
  baseUrlOf,
  betterAddress,
  isBestAddress,
  kindOf,
  moveAddress,
  onAddressChange,
  pickAddress,
  probeAddress,
  raceAddresses,
  repickAddress,
  useAddress,
  type MachineAddress,
  type ProbeOutcome,
} from "@/lib/machine-routes";
import { fetchOnMachine, type MachineConnection } from "@/lib/machines";

const NAME = "http://studio.tail4f2a.ts.net:2113";
const TAIL_IP = "http://100.88.14.3:2113";
const LAN = "http://192.168.1.42:2113";
const HOST = "http://studio:2113";

function machine(baseUrl = NAME, urls = [NAME, TAIL_IP, LAN, HOST]): MachineConnection {
  const addresses: MachineAddress[] = urls.map((url, i) => ({ url, kind: kindOf(url), learned: i > 0 }));
  return { id: "machine-studio", name: "studio", baseUrl, token: "fmt_studio", addedAt: "2026-11-06T09:00:00Z", addresses };
}

/** A network where the machine answers at some addresses, someone else at others, and nothing anywhere else. */
function network(answers: Record<string, ProbeOutcome>) {
  const asked: string[] = [];
  const probe = async (url: string) => {
    asked.push(url);
    return answers[url] ?? "no-answer";
  };
  return { asked, probe };
}

beforeEach(() => {
  localStorage.clear();
  _resetForTesting();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe("kinds and order", () => {
  it.each([
    [NAME, "tailnet"],
    [TAIL_IP, "tailnet-ip"],
    [LAN, "lan"],
    ["http://10.0.0.5:2113", "lan"],
    ["http://172.20.0.3:2113", "lan"],
    ["http://172.32.0.3:2113", "other"],
    ["http://[fd12:3456::1]:2113", "lan"],
    ["http://[fd7a:115c:a1e0::1]:2113", "tailnet-ip"],
    [HOST, "hostname"],
    ["http://studio.local:2113", "hostname"],
    ["https://fleet.example.com", "other"],
    ["http://203.0.113.9:2113", "other"],
    ["http://localhost:40123", "other"],
  ] as const)("%s is %s", (url, kind) => {
    expect(kindOf(url)).toBe(kind);
  });

  it("moves an address up and down the list", () => {
    expect(moveAddress(["a", "b", "c", "d"], 2, 0)).toEqual(["c", "a", "b", "d"]);
    expect(moveAddress(["a", "b", "c", "d"], 0, 3)).toEqual(["b", "c", "d", "a"]);
    expect(moveAddress(["a", "b"], 1, -4)).toEqual(["b", "a"]);
    expect(moveAddress(["a", "b"], 5, 0)).toEqual(["a", "b"]);
  });
});

describe("the address in use", () => {
  it("is home's until this tab picks one, and the pick is remembered", () => {
    const studio = machine(NAME);
    expect(baseUrlOf(studio)).toBe(NAME);

    useAddress(studio.id, LAN);

    expect(baseUrlOf(studio)).toBe(LAN);
    _resetForTesting();
    expect(baseUrlOf(studio)).toBe(LAN);
  });

  it("is home's again once the picked address is no longer one of the machine's", () => {
    useAddress("machine-studio", "http://192.168.1.99:2113");
    expect(baseUrlOf(machine(NAME))).toBe(NAME);
  });

  it("tells listeners when it moves", () => {
    const moves: string[] = [];
    onAddressChange((_, url) => moves.push(url));

    useAddress("machine-studio", LAN);
    useAddress("machine-studio", LAN);

    expect(moves).toEqual([LAN]);
  });

  it("is the best when it's the first address", () => {
    expect(isBestAddress(machine(NAME))).toBe(true);
    expect(isBestAddress(machine(LAN))).toBe(false);
    expect(isBestAddress(machine(NAME, [NAME]))).toBe(true);
  });
});

describe("raceAddresses", () => {
  it("takes the first in order even when a later one answers sooner", async () => {
    const { winner } = await raceAddresses(["a", "b"], (url) => new Promise((resolve) => setTimeout(() => resolve("ok"), url === "a" ? 60 : 5)), 10);
    expect(winner).toBe("a");
  });

  it("moves on as soon as the ones above have failed, without waiting out the gap", async () => {
    const started: string[] = [];
    const began = Date.now();

    const { winner, outcomes } = await raceAddresses(["a", "b", "c"], async (url) => {
      started.push(url);
      return url === "c" ? "ok" : "no-answer";
    }, 60_000);

    expect(winner).toBe("c");
    expect(started).toEqual(["a", "b", "c"]);
    expect(outcomes).toEqual({ a: "no-answer", b: "no-answer", c: "ok" });
    expect(Date.now() - began).toBeLessThan(5000);
  });

  it("starts the next before a slow one gives up, and waits for the slow one above it", async () => {
    let answerSlow: (outcome: ProbeOutcome) => void = () => {};
    const started: string[] = [];
    let settled = false;

    const race = raceAddresses(["slow", "fast"], (url) => {
      started.push(url);
      return url === "slow" ? new Promise((resolve) => { answerSlow = resolve; }) : Promise.resolve("ok");
    }, 10).then((result) => {
      settled = true;
      return result;
    });

    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(started).toEqual(["slow", "fast"]);
    expect(settled).toBe(false);
    answerSlow("no-answer");
    expect((await race).winner).toBe("fast");
  });

  it("never takes another machine, or one that can't say who it is", async () => {
    const { winner } = await raceAddresses(["a", "b"], async (url) => url === "a" ? "other-machine" : "no-probe", 10);
    expect(winner).toBeNull();
  });
});

describe("pickAddress", () => {
  it("keeps the address in use when it answers, and asks nothing else", async () => {
    const { asked, probe } = network({ [NAME]: "ok" });

    expect(await pickAddress(machine(NAME), probe)).toBe(NAME);
    expect(asked).toEqual([NAME]);
  });

  it("falls back down the list when Tailscale doesn't answer, and starts there next time", async () => {
    const { probe } = network({ [LAN]: "ok", [HOST]: "ok" });
    const studio = machine(NAME);

    expect(await pickAddress(studio, probe)).toBe(LAN);
    expect(baseUrlOf(studio)).toBe(LAN);

    const again = network({ [LAN]: "ok" });
    expect(await pickAddress(studio, again.probe)).toBe(LAN);
    expect(again.asked).toEqual([LAN]);
  });

  it("skips an address where another Fleet answers", async () => {
    const { probe } = network({ [TAIL_IP]: "other-machine", [LAN]: "ok" });
    expect(await pickAddress(machine(NAME), probe)).toBe(LAN);
  });

  it("stays put when nothing answers, so the request after it says why", async () => {
    const { probe } = network({});
    expect(await pickAddress(machine(NAME), probe)).toBe(NAME);
  });

  it("trusts the address home uses when the Fleet there is too old to say who it is", async () => {
    const { asked, probe } = network({ [NAME]: "no-probe" });
    expect(await pickAddress(machine(NAME), probe)).toBe(NAME);
    expect(asked).toEqual([NAME]);
  });

  it("doesn't probe a machine with one address", async () => {
    const { asked, probe } = network({});
    expect(await pickAddress(machine(NAME, [NAME]), probe)).toBe(NAME);
    expect(asked).toEqual([]);
  });

  it("shares one pick between callers that ask at once", async () => {
    const { asked, probe } = network({ [NAME]: "ok" });
    const studio = machine(NAME);

    await Promise.all([pickAddress(studio, probe), pickAddress(studio, probe)]);

    expect(asked).toEqual([NAME]);
  });
});

describe("betterAddress", () => {
  it("finds a higher address that answers again, without moving to it", async () => {
    useAddress("machine-studio", LAN);
    const { asked, probe } = network({ [TAIL_IP]: "ok", [LAN]: "ok" });
    const studio = machine(NAME);

    expect(await betterAddress(studio, probe)).toBe(TAIL_IP);
    expect(asked).not.toContain(LAN);
    expect(asked).not.toContain(HOST);
    expect(baseUrlOf(studio)).toBe(LAN);
  });

  it("has nothing to look for on the first address", async () => {
    const { asked, probe } = network({ [NAME]: "ok" });
    expect(await betterAddress(machine(NAME), probe)).toBeNull();
    expect(asked).toEqual([]);
  });
});

describe("probeAddress", () => {
  it("asks who answers without a token or cookies, and checks the id", async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ id: "machine-studio", apiVersion: 1 }), { headers: { "Content-Type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);

    expect(await probeAddress(LAN, "machine-studio")).toBe("ok");
    expect(await probeAddress(LAN, "machine-other")).toBe("other-machine");

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe(`${LAN}/api/machine/id`);
    expect(init.credentials).toBe("omit");
    expect(new Headers(init.headers).has("Authorization")).toBe(false);
  });

  it("says no answer when the address doesn't answer in time", async () => {
    vi.stubGlobal("fetch", vi.fn((_url: string, init: RequestInit) => new Promise((_, reject) => {
      init.signal?.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")));
    })));

    expect(await probeAddress(NAME, "machine-studio", 20)).toBe("no-answer");
  });

  it("says no probe for a Fleet too old to have one", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response("<html></html>", { headers: { "Content-Type": "text/html" } })));
    expect(await probeAddress(NAME, "machine-studio")).toBe("no-probe");
  });
});

describe("requests that can't reach the machine", () => {
  it("go again on the address that answers, and the token only goes where the machine answered", async () => {
    const studio = machine(NAME);
    const calls: { url: string; token: string | null }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (input: string, init?: RequestInit) => {
      const url = String(input);
      calls.push({ url, token: new Headers(init?.headers).get("Authorization") });
      if (url.endsWith("/api/machine/id")) {
        if (url.startsWith(TAIL_IP)) return Response.json({ id: "machine-someone-else", apiVersion: 1 });
        if (url.startsWith(LAN)) return Response.json({ id: "machine-studio", apiVersion: 1 });
        throw new TypeError("Failed to fetch");
      }
      if (url.startsWith(LAN)) return Response.json({ sessions: [] });
      throw new TypeError("Failed to fetch");
    }));

    const response = await fetchOnMachine(studio, "/api/sessions");

    expect(response.ok).toBe(true);
    expect(baseUrlOf(studio)).toBe(LAN);
    const withToken = calls.filter((call) => call.token !== null).map((call) => call.url);
    expect(withToken).toEqual([`${NAME}/api/sessions`, `${LAN}/api/sessions`]);
  });

  it("aren't sent again when they change something", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => {
      throw new TypeError("Failed to fetch");
    }));

    await expect(fetchOnMachine(machine(NAME), "/api/sessions", { method: "POST" })).rejects.toThrow("Failed to fetch");
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it("repick says whether the address moved", async () => {
    vi.stubGlobal("fetch", vi.fn(async (input: string) => {
      if (String(input).startsWith(NAME)) return Response.json({ id: "machine-studio", apiVersion: 1 });
      throw new TypeError("Failed to fetch");
    }));
    expect(await repickAddress(machine(NAME))).toBe(false);
  });
});
