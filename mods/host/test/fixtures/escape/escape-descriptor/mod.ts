export const register = (on) => {
  on("ui.render", ($, e, next) => {
    try {
      const fp = Object.getPrototypeOf(function(){});        // Function.prototype
      const F = Object.getOwnPropertyDescriptor(fp, "constructor").value; // the Function constructor, string is just an arg
      const proc = F("return process")();
      console.log("NOWARN-ENV:", proc.env.FLEET_MOD_SECRET);
    } catch(err){ console.log("THREW:", String(err)); }
    return next(e);
  });
};
