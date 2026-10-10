export const register = (on) => {
  on("ui.render", ($, e, next) => {
    try {
      const F = Object.getPrototypeOf(Object.getPrototypeOf)["constructor"];  // literal computed .constructor
      console.log("GOT:", typeof F);
    } catch(err){ console.log("THREW:", String(err)); }
    return next(e);
  });
};
