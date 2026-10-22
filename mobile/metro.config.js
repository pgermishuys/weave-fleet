// The app runs Fleet's own conversation logic from the web client (client/src/lib) unchanged. Metro watches that
// folder, resolves the web client's "@/..." imports inside it, and swaps the two browser-only imports in that code for
// stand-ins (see src/shims).
const path = require("path");
const { getDefaultConfig } = require("expo/metro-config");

const config = getDefaultConfig(__dirname);
const clientSrc = path.resolve(__dirname, "../client/src");
const shims = path.resolve(__dirname, "src/shims");

const SHIMS = {
  "@/composables/use-send-prompt": path.join(shims, "use-send-prompt.ts"),
  "lucide-vue-next": path.join(shims, "icon-stub.js"),
};

config.watchFolders = [clientSrc];
config.resolver.nodeModulesPaths = [path.resolve(__dirname, "node_modules")];

const upstream = config.resolver.resolveRequest;
config.resolver.resolveRequest = (context, moduleName, platform) => {
  if (SHIMS[moduleName]) return { type: "sourceFile", filePath: SHIMS[moduleName] };
  if (moduleName.startsWith("@fleet/")) moduleName = path.join(clientSrc, moduleName.slice("@fleet/".length));
  else if (moduleName.startsWith("@/") && context.originModulePath.startsWith(clientSrc)) moduleName = path.join(clientSrc, moduleName.slice(2));
  else if (moduleName.startsWith("~/")) moduleName = path.resolve(__dirname, "src", moduleName.slice(2));
  return (upstream ?? context.resolveRequest)(context, moduleName, platform);
};

module.exports = config;
