import type { Register } from "fleet-mods";

// Shows how a mod opens its own tab: the opened body of a shell row gets an "Open board" button, and the board is a
// Pane the mod draws itself, with a "Close" button that closes the tab. mod.json declares the pane too, so it is also in the + menu.
const ITEMS = [
  { title: "Cut the release branch", state: "Done", tone: "good" },
  { title: "Smoke test on staging", state: "Running", tone: "warn" },
  { title: "Write the release notes", state: "To do", tone: "neutral" },
] as const;

export const register: Register = (on) => {
  on("ui.render", { component: "ToolResult", props: { tool: ["bash", "shell"] } }, async ($, e, next) => {
    if (e.component !== "ToolResult") return next(e);
    const { Box, Button } = $.ui.resolve(e);
    return Box({
      flexDirection: "column",
      gap: 2,
      children: [
        Button({
          key: "open",
          label: "Open board",
          tone: "primary",
          onPress: async () => {
            await $.ui.open({ id: "board", title: "Release board" });
          },
        }),
        await next(e),
      ],
    });
  });

  on("ui.render", { component: "Pane" }, async ($, e, next) => {
    if (e.component !== "Pane" || e.requestId !== "board") return next(e);
    const { Box, Text, Pill, Button } = $.ui.resolve(e);
    return Box({
      flexDirection: "column",
      gap: 3,
      children: [
        Text({ bold: true, children: ["Release 2.4"] }),
        ...ITEMS.map((item) =>
          Box({
            flexDirection: "row",
            gap: 2,
            alignItems: "center",
            children: [Pill({ tone: item.tone, label: item.state }), Text({ children: [item.title] })],
          }),
        ),
        Box({
          flexDirection: "row",
          children: [
            Button({
              key: "close",
              label: "Close",
              onPress: async () => {
                await $.ui.close({ id: "board" });
              },
            }),
          ],
        }),
      ],
    });
  });
};
