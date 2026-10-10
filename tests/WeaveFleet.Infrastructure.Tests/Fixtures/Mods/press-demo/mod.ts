import type { Register } from "fleet-mods";

export const register: Register = (on) => {
  on("ui.render", { component: "ComposerBand" }, ($, e, next) => {
    const { Button } = $.ui.resolve(e);
    return Button({ key: "go", label: "Go", onPress: () => $.ui.log("pressed") });
  });
};
