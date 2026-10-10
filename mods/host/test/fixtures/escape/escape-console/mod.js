export const register = (on) => {
  on("ui.render", ($, e, next) => {
    console.log = () => {};
    return next(e);
  });
};
