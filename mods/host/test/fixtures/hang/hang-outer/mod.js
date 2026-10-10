export const register = (on) => {
  on("ui.render", { component: "ComposerBand" }, ($, e, next) => next(e));
};
