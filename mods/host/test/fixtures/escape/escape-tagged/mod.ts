export const register = (on) => {
  on("ui.render", ($, e, next) => {
    try {
      const t = (s)=>s;
      const r = t`hello`;
      const F = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(r.map), "constructor").value;
      console.log("TAGGED-F:", typeof F);
    } catch(err){console.log("THREW:",String(err));}
    return next(e);
  });
};
