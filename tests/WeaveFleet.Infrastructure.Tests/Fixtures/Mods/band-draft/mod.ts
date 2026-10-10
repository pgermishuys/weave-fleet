import type { Register } from "fleet-mods";

export const register: Register = (on) => {
  on("ui.render", { component: "ComposerBand" }, ($, e, next) => {
    const { Text } = $.ui.resolve(e);
    return Text({ children: ["first save"] });
  });
};
