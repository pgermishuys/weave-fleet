import * as os from "node:os";
import * as path from "node:path";

/** Where Fleet keeps its data, and where the app finds the server it starts. */
export interface FleetPaths {
  /** The data directory, shared with CLI installs: `~/.weave` unless overridden. */
  dataDir: string;
  databasePath: string;
  /** Written by a running Fleet next to its database (see FleetInstanceLock on the server). */
  instanceFile: string;
  /** The Fleet binary the app starts when no Fleet is running. */
  serverBinary: string;
  /** Where the server finds its UI (`wwwroot`) and appsettings. */
  serverContentRoot: string;
}

export interface PathInputs {
  env: NodeJS.ProcessEnv;
  platform: NodeJS.Platform;
  homeDir?: string;
  /** Electron's `process.resourcesPath` when packaged, otherwise null. */
  resourcesPath: string | null;
  /** The desktop package directory, for development runs against a repo build. */
  appDir: string;
}

export function resolvePaths(inputs: PathInputs): FleetPaths {
  const { env, platform, resourcesPath, appDir } = inputs;
  const home = inputs.homeDir ?? os.homedir();
  const dataDir = env.FLEET_DESKTOP_DATA_DIR || path.join(home, ".weave");
  const exe = platform === "win32" ? "WeaveFleet.Api.exe" : "WeaveFleet.Api";

  let serverBinary: string;
  let serverContentRoot: string;
  if (env.FLEET_DESKTOP_SERVER_BIN) {
    serverBinary = env.FLEET_DESKTOP_SERVER_BIN;
    serverContentRoot = env.FLEET_DESKTOP_SERVER_CONTENT_ROOT || path.dirname(serverBinary);
  } else if (resourcesPath) {
    // The AOT publish output ships under resources/fleet, outside the asar so it stays executable.
    serverContentRoot = path.join(resourcesPath, "fleet");
    serverBinary = path.join(serverContentRoot, exe);
  } else {
    // Development: the repo's Release build, with the UI from src/WeaveFleet.Api/wwwroot.
    const repoRoot = path.resolve(appDir, "..");
    const rid = platform === "win32" ? "win-x64" : platform === "darwin" ? "osx-arm64" : "linux-x64";
    serverBinary = path.join(repoRoot, "src", "WeaveFleet.Api", "bin", "Release", "net10.0", rid, exe);
    serverContentRoot = path.join(repoRoot, "src", "WeaveFleet.Api");
  }

  return {
    dataDir,
    databasePath: path.join(dataDir, "fleet.db"),
    instanceFile: path.join(dataDir, "fleet.instance.json"),
    serverBinary,
    serverContentRoot,
  };
}
