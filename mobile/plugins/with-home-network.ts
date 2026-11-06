// Plain http where a machine's other addresses are: Tailscale (encrypted by Tailscale itself), the home network, and
// this device (the walk-through's Fleet, reached through `adb reverse`). Everything else stays https only. Neither
// platform can name address ranges, so the app opens plain http here and the range check is the app's own:
// `canUse` in src/fleet/routes-core.ts, and a token only goes where the machine's id answered (`/api/machine/id`).
// Android: cleartext allowed by the network security config (the app's check decides where).
// iOS: NSAllowsLocalNetworking covers IP addresses and bare host names; Tailscale's names (*.ts.net) get their own
// exception; the Local Network prompt explains why Fleet looks on the home network.
import { type ConfigPlugin, withAndroidManifest, withDangerousMod, withInfoPlist } from "expo/config-plugins";
import fs from "node:fs";
import path from "node:path";

const CONFIG = `<?xml version="1.0" encoding="utf-8"?>
<network-security-config>
  <base-config cleartextTrafficPermitted="true" />
</network-security-config>
`;

const withHomeNetwork: ConfigPlugin = (config) => {
  config = withDangerousMod(config, [
    "android",
    async (c) => {
      const dir = path.join(c.modRequest.platformProjectRoot, "app/src/main/res/xml");
      fs.mkdirSync(dir, { recursive: true });
      fs.writeFileSync(path.join(dir, "network_security_config.xml"), CONFIG);
      return c;
    },
  ]);
  config = withAndroidManifest(config, (c) => {
    const app = c.modResults.manifest.application?.[0];
    if (app) app.$["android:networkSecurityConfig"] = "@xml/network_security_config";
    return c;
  });
  return withInfoPlist(config, (c) => {
    const ats = c.modResults.NSAppTransportSecurity ?? {};
    c.modResults.NSAppTransportSecurity = {
      ...ats,
      NSAllowsLocalNetworking: true,
      NSExceptionDomains: { ...ats.NSExceptionDomains, "ts.net": { NSIncludesSubdomains: true, NSExceptionAllowsInsecureHTTPLoads: true } },
    };
    c.modResults.NSLocalNetworkUsageDescription = "Fleet reaches your computer on your home network when Tailscale isn't on.";
    return c;
  });
};

export default withHomeNetwork;
