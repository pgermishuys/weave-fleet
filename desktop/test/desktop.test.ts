import * as net from "node:net";
import { describe, expect, it } from "vitest";
import { isLoopbackHttpUrl, parseInstance } from "../src/instance";
import { classifyLink } from "../src/links";
import { resolvePaths } from "../src/paths";
import { buildServerEnv, pickPort, restartDelay } from "../src/server";
import { restorableBounds } from "../src/settings";
import { captureCommand, mergePath, parseCaptured } from "../src/shell-env";

const linuxPaths = (env: NodeJS.ProcessEnv = {}, resourcesPath: string | null = null) =>
  resolvePaths({ env, platform: "linux", homeDir: "/home/me", resourcesPath, appDir: "/repo/desktop" });

describe("paths", () => {
  it("shares ~/.weave with CLI installs", () => {
    const paths = linuxPaths();
    expect(paths.dataDir).toBe("/home/me/.weave");
    expect(paths.databasePath).toBe("/home/me/.weave/fleet.db");
    expect(paths.instanceFile).toBe("/home/me/.weave/fleet.instance.json");
  });

  it("uses the bundled server when packaged", () => {
    const paths = linuxPaths({}, "/opt/Fleet/resources");
    expect(paths.serverBinary).toBe("/opt/Fleet/resources/fleet/WeaveFleet.Api");
    expect(paths.serverContentRoot).toBe("/opt/Fleet/resources/fleet");
  });

  it("uses the repo's Release build in development", () => {
    const paths = linuxPaths();
    expect(paths.serverBinary).toBe("/repo/src/WeaveFleet.Api/bin/Release/net10.0/linux-x64/WeaveFleet.Api");
    expect(paths.serverContentRoot).toBe("/repo/src/WeaveFleet.Api");
  });

  it("takes overrides for scratch runs", () => {
    const paths = linuxPaths({ FLEET_DESKTOP_DATA_DIR: "/scratch/data", FLEET_DESKTOP_SERVER_BIN: "/scratch/bin/WeaveFleet.Api" });
    expect(paths.databasePath).toBe("/scratch/data/fleet.db");
    expect(paths.serverBinary).toBe("/scratch/bin/WeaveFleet.Api");
    expect(paths.serverContentRoot).toBe("/scratch/bin");
  });

  it("names the Windows binary with .exe", () => {
    const paths = resolvePaths({ env: {}, platform: "win32", homeDir: "C:\\Users\\me", resourcesPath: "C:\\Fleet\\resources", appDir: "C:\\repo\\desktop" });
    expect(paths.serverBinary.endsWith("WeaveFleet.Api.exe")).toBe(true);
  });
});

describe("instance file", () => {
  const valid = { pid: 42, url: "http://127.0.0.1:2113/", version: "0.24.0", databasePath: "/home/me/.weave/fleet.db", desktop: false, startedAt: "2026-09-14T12:00:00Z" };

  it("reads what the server writes", () => {
    expect(parseInstance(JSON.stringify(valid))).toEqual({ ...valid, url: "http://127.0.0.1:2113" });
  });

  it.each([
    ["not JSON", "{"],
    ["no pid", JSON.stringify({ ...valid, pid: undefined })],
    ["a negative pid", JSON.stringify({ ...valid, pid: -1 })],
    ["a remote URL", JSON.stringify({ ...valid, url: "http://evil.example:80" })],
    ["an https URL", JSON.stringify({ ...valid, url: "https://127.0.0.1:2113" })],
    ["a file URL", JSON.stringify({ ...valid, url: "file:///etc/passwd" })],
  ])("rejects %s", (_name, json) => {
    expect(parseInstance(json)).toBeNull();
  });

  it("only trusts loopback URLs", () => {
    expect(isLoopbackHttpUrl("http://127.0.0.1:5000")).toBe(true);
    expect(isLoopbackHttpUrl("http://[::1]:5000")).toBe(true);
    expect(isLoopbackHttpUrl("http://localhost:5000")).toBe(true);
    expect(isLoopbackHttpUrl("http://192.168.1.10:5000")).toBe(false);
  });
});

describe("links", () => {
  const origin = "http://127.0.0.1:5000";

  it("keeps Fleet's own pages in the window", () => {
    expect(classifyLink("http://127.0.0.1:5000/sessions/abc", origin)).toBe("window");
  });

  it("opens web and mail links in the browser", () => {
    expect(classifyLink("https://github.com/pgermishuys/weave-fleet/pull/1", origin)).toBe("browser");
    expect(classifyLink("http://127.0.0.1:41000/", origin)).toBe("browser");
    expect(classifyLink("mailto:someone@example.com", origin)).toBe("browser");
  });

  it("blocks everything else", () => {
    expect(classifyLink("javascript:alert(1)", origin)).toBe("block");
    expect(classifyLink("file:///etc/passwd", origin)).toBe("block");
    expect(classifyLink("vscode://open", origin)).toBe("block");
    expect(classifyLink("not a url", origin)).toBe("block");
  });
});

describe("server environment", () => {
  const paths = linuxPaths();

  it("runs Fleet in desktop mode on the shared data directory", () => {
    const env = buildServerEnv({ PATH: "/usr/bin", HOME: "/home/me" }, paths, 5123);
    expect(env).toMatchObject({
      PATH: "/usr/bin",
      HOME: "/home/me",
      ASPNETCORE_ENVIRONMENT: "Production",
      Fleet__Host: "127.0.0.1",
      Fleet__Port: "5123",
      Fleet__DatabasePath: "/home/me/.weave/fleet.db",
      Fleet__AnalyticsDatabasePath: "/home/me/.weave/fleet-analytics.db",
      Fleet__DataProtection__KeyPath: "/home/me/.weave/fleet-keys",
      Fleet__Desktop__Enabled: "true",
    });
  });

  it("drops Electron's variables and inherited listen URLs", () => {
    const env = buildServerEnv(
      { ELECTRON_RUN_AS_NODE: "1", electron_no_asar: "1", URLS: "http://0.0.0.0:80", ASPNETCORE_URLS: "x", ASPNETCORE_ENVIRONMENT: "Development", KEEP: "yes" },
      paths,
      5000,
    );
    expect(env.ELECTRON_RUN_AS_NODE).toBeUndefined();
    expect(env.electron_no_asar).toBeUndefined();
    expect(env.URLS).toBeUndefined();
    expect(env.ASPNETCORE_URLS).toBeUndefined();
    expect(env.ASPNETCORE_ENVIRONMENT).toBe("Production");
    expect(env.KEEP).toBe("yes");
  });
});

describe("restarts", () => {
  const now = 1_000_000;

  it("restarts straight away after the first crash, then backs off", () => {
    expect(restartDelay([now], now)).toBe(0);
    expect(restartDelay([now - 3000, now], now)).toBe(1000);
    expect(restartDelay([now - 6000, now - 3000, now], now)).toBe(2000);
    expect(restartDelay([now - 9000, now - 6000, now - 3000, now], now)).toBe(4000);
  });

  it("gives up after five crashes in a minute", () => {
    expect(restartDelay([now - 40_000, now - 30_000, now - 20_000, now - 10_000, now], now)).toBeNull();
  });

  it("forgets crashes older than a minute", () => {
    expect(restartDelay([now - 300_000, now - 200_000, now - 100_000, now - 90_000, now], now)).toBe(0);
  });
});

describe("ports", () => {
  it("keeps the preferred port when it's free", async () => {
    const free = await pickPort(undefined);
    expect(await pickPort(free)).toBe(free);
  });

  it("picks another port when the preferred one is taken", async () => {
    const blocker = net.createServer();
    await new Promise<void>((resolve) => blocker.listen(0, "127.0.0.1", resolve));
    const taken = (blocker.address() as net.AddressInfo).port;
    try {
      const picked = await pickPort(taken);
      expect(picked).not.toBe(taken);
      expect(picked).toBeGreaterThan(0);
    } finally {
      blocker.close();
    }
  });
});

describe("window bounds", () => {
  const screens = [{ x: 0, y: 0, width: 1920, height: 1080 }];

  it("restores bounds that are still on a screen", () => {
    const saved = { x: 100, y: 100, width: 1200, height: 800 };
    expect(restorableBounds(saved, screens)).toEqual(saved);
  });

  it("drops bounds on a monitor that's gone", () => {
    expect(restorableBounds({ x: 2500, y: 100, width: 1200, height: 800 }, screens)).toBeNull();
  });

  it("drops bounds too small to use", () => {
    expect(restorableBounds({ x: 0, y: 0, width: 200, height: 100 }, screens)).toBeNull();
  });
});

describe("login shell environment", () => {
  it("reads each variable between its markers, ignoring shell noise", () => {
    const names = ["PATH", "SSH_AUTH_SOCK", "LANG"];
    const output = [
      "Welcome to your shell!",
      "__FLEET_ENV_PATH_START__",
      "/opt/homebrew/bin:/usr/bin",
      "__FLEET_ENV_PATH_END__",
      "__FLEET_ENV_SSH_AUTH_SOCK_START__",
      "__FLEET_ENV_SSH_AUTH_SOCK_END__",
      "__FLEET_ENV_LANG_START__",
      "en_GB.UTF-8",
      "__FLEET_ENV_LANG_END__",
    ].join("\n");
    expect(parseCaptured(output, names)).toEqual({ PATH: "/opt/homebrew/bin:/usr/bin", LANG: "en_GB.UTF-8" });
  });

  it("asks the shell with printenv for each name", () => {
    expect(captureCommand(["PATH"])).toBe("printf '%s\\n' '__FLEET_ENV_PATH_START__'; printenv PATH || true; printf '%s\\n' '__FLEET_ENV_PATH_END__'");
  });

  it("puts the shell's PATH first and keeps the app's extra entries", () => {
    expect(mergePath("/opt/homebrew/bin:/usr/bin", "/usr/bin:/snap/bin", ":")).toBe("/opt/homebrew/bin:/usr/bin:/snap/bin");
    expect(mergePath(undefined, "/usr/bin", ":")).toBe("/usr/bin");
    expect(mergePath(undefined, undefined, ":")).toBeUndefined();
  });
});
