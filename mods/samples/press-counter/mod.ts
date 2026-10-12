import type { Register } from "fleet-mods";

// Shows how a mod's buttons work: the opened body of a shell row gets a "Count a press" button and the count so far.
// The count lives in $.state, so writing it draws the row again, and each session counts for itself.
export const register: Register = (on) => {
  on("ui.render", { component: "ToolResult", props: { tool: ["bash", "shell"] } }, async ($, e, next) => {
    if (e.component !== "ToolResult") return next(e);
    const { Box, Text, Button } = $.ui.resolve(e);
    const presses = $.state.get<number>("presses") ?? 0;
    return Box({
      flexDirection: "column",
      gap: 2,
      children: [
        Box({
          flexDirection: "row",
          gap: 2,
          alignItems: "center",
          children: [
            Button({
              key: "count",
              label: "Count a press",
              tone: "primary",
              onPress: () => {
                $.state.set("presses", ($.state.get<number>("presses") ?? 0) + 1);
              },
            }),
            Text({ dimColor: true, children: [`Pressed ${presses} ${presses === 1 ? "time" : "times"}`] }),
          ],
        }),
        await next(e),
      ],
    });
  });
};
