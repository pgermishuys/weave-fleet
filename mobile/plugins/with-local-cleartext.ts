// The walk-through build talks plain http to the scratch Fleet on this device (reached through `adb reverse`).
// Android: cleartext only to localhost and 127.0.0.1, not usesCleartextTraffic for everything.
// iOS: NSAllowsLocalNetworking keeps ATS on for every other host.
import { type ConfigPlugin, withAndroidManifest, withDangerousMod, withInfoPlist } from "expo/config-plugins";
import fs from "node:fs";
import path from "node:path";

const CONFIG = `<?xml version="1.0" encoding="utf-8"?>
<network-security-config>
  <domain-config cleartextTrafficPermitted="true">
    <domain includeSubdomains="false">localhost</domain>
    <domain includeSubdomains="false">127.0.0.1</domain>
  </domain-config>
</network-security-config>
`;

const withLocalCleartext: ConfigPlugin = (config) => {
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
    c.modResults.NSAppTransportSecurity = { ...c.modResults.NSAppTransportSecurity, NSAllowsLocalNetworking: true };
    return c;
  });
};

export default withLocalCleartext;
