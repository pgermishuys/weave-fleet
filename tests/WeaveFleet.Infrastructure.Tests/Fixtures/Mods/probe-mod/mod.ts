import type { Register } from "fleet-mods";

export const register: Register = (on) => {
  on("session.start", async ($, e, next) => {
    $.ui.log(`start ${e.reason}`);
    const starts = ((await $.store.get("starts")) as number | undefined) ?? 0;
    await $.store.set("starts", starts + 1);
    return next(e);
  });

  on("ui.render", { component: "StatusChip" }, async ($, e, next) => {
    const { Text } = $.ui.resolve(e);
    const starts = await $.store.get("starts");
    const title = await $.session.title();
    return Text({ children: [`${starts} starts in ${title}`] });
  });
};
