// Packages the app with the Fleet server inside it. FLEET_SERVER_DIR is a `dotnet publish` of WeaveFleet.Api for the
// target platform (the release build's AOT output); it ships under resources/fleet, outside the asar archive so the
// binary stays executable, and paths.ts finds it there.
const path = require("node:path");

const serverDir = process.env.FLEET_SERVER_DIR;
if (!serverDir) throw new Error("Set FLEET_SERVER_DIR to a `dotnet publish` of WeaveFleet.Api for the target platform.");

/** @type {import("electron-builder").Configuration} */
module.exports = {
  appId: "com.pgermishuys.fleet",
  productName: "Fleet",
  artifactName: "Fleet-${version}-${os}-${arch}.${ext}",
  directories: { output: "release", buildResources: "build" },
  files: ["dist/**/*.js", "static/**/*", "package.json"],
  extraResources: [{ from: path.resolve(serverDir), to: "fleet", filter: ["**/*", "!**/*.pdb", "!**/*.dbg"] }],
  electronLanguages: ["en-US"],
  npmRebuild: false,
  // Where electron-updater looks for new versions: the public mirror the CLI updater reads too. The release workflow
  // uploads the files itself (`--publish never`); this only writes the update feed (latest*.yml) and app-update.yml.
  publish: [{ provider: "github", owner: "pgermishuys", repo: "fleet-releases", releaseType: "release" }],

  mac: {
    target: ["dmg", "zip"],
    category: "public.app-category.developer-tools",
    // Ad-hoc signing until a Developer ID exists (CSC_LINK): Apple Silicon won't run an unsigned app at all, and
    // ad-hoc gets "unidentified developer" (Open Anyway) instead of "damaged". Hardened runtime is only for
    // notarisation, which needs the real certificate.
    identity: process.env.CSC_LINK ? undefined : "-",
    hardenedRuntime: Boolean(process.env.CSC_LINK),
  },
  dmg: { title: "Fleet ${version}" },

  win: { target: ["nsis"] },
  nsis: { artifactName: "Fleet-${version}-win-${arch}-setup.${ext}" },

  linux: {
    target: ["AppImage", "deb"],
    executableName: "fleet-desktop",
    category: "Development",
    synopsis: "Run and watch many AI coding agents at once",
    desktop: { entry: { StartupWMClass: "Fleet" } },
  },
};
