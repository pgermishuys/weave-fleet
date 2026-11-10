import { Menu, clipboard, shell, type BrowserWindow, type ContextMenuParams, type MenuItemConstructorOptions } from "electron";

export interface MenuActions {
  openLogs: () => void;
  openDataFolder: () => void;
  checkForUpdates: () => void;
  reportProblem: () => void;
  findInConversation: () => void;
}

/** The application menu. The Edit roles are what make copy and paste work on macOS. */
export function buildAppMenu(platform: NodeJS.Platform, actions: MenuActions): Menu {
  const mac = platform === "darwin";
  const template: MenuItemConstructorOptions[] = [
    ...(mac
      ? [
          {
            label: "Fleet",
            submenu: [
              { role: "about" as const },
              { label: "Check for Updates…", click: actions.checkForUpdates },
              { type: "separator" as const },
              { role: "services" as const },
              { type: "separator" as const },
              { role: "hide" as const },
              { role: "hideOthers" as const },
              { role: "unhide" as const },
              { type: "separator" as const },
              { role: "quit" as const },
            ],
          },
        ]
      : [{ label: "File", submenu: [{ role: "quit" as const, label: "Quit Fleet" }] }]),
    {
      label: "Edit",
      submenu: [
        { role: "undo" },
        { role: "redo" },
        { type: "separator" },
        { role: "cut" },
        { role: "copy" },
        { role: "paste" },
        { role: "selectAll" },
        { type: "separator" },
        // Fleet's find bar takes Ctrl/⌘F from the conversation and its composer, while the editor and the terminal keep
        // the key. So the key must reach the page: macOS and Linux show the accelerator without registering it
        // (`registerAccelerator: false`). Windows registers every menu accelerator and doesn't honour that option, so
        // there the key is only shown, as text after a tab, and the page handles the key. Choosing the item with the
        // mouse always opens the bar.
        platform !== "win32"
          ? { label: "Find in Conversation", accelerator: "CommandOrControl+F", registerAccelerator: false, click: actions.findInConversation }
          : { label: "Find in Conversation\tCtrl+F", click: actions.findInConversation },
      ],
    },
    {
      label: "View",
      submenu: [
        { role: "reload" },
        { role: "forceReload" },
        { role: "toggleDevTools" },
        { type: "separator" },
        { role: "resetZoom" },
        { role: "zoomIn" },
        { role: "zoomOut" },
        { type: "separator" },
        { role: "togglefullscreen" },
      ],
    },
    { role: "windowMenu" },
    {
      role: "help",
      submenu: [
        { label: "Report a Problem…", click: actions.reportProblem },
        { type: "separator" as const },
        ...(mac ? [] : [{ label: "Check for Updates…", click: actions.checkForUpdates }, { type: "separator" as const }]),
        { label: "Open Logs Folder", click: actions.openLogs },
        { label: "Open Fleet Data Folder", click: actions.openDataFolder },
      ],
    },
  ];
  return Menu.buildFromTemplate(template);
}

/** Right-click: spelling suggestions, cut/copy/paste in fields, copy for selections, and link actions. */
export function showContextMenu(window: BrowserWindow, params: ContextMenuParams): void {
  const items: MenuItemConstructorOptions[] = [];

  for (const suggestion of params.dictionarySuggestions.slice(0, 5)) {
    items.push({ label: suggestion, click: () => window.webContents.replaceMisspelling(suggestion) });
  }
  if (params.misspelledWord) {
    items.push(
      { label: "Add to Dictionary", click: () => window.webContents.session.addWordToSpellCheckerDictionary(params.misspelledWord) },
      { type: "separator" },
    );
  }

  if (params.linkURL && /^(https?|mailto):/i.test(params.linkURL)) {
    items.push(
      { label: "Open Link in Browser", click: () => void shell.openExternal(params.linkURL) },
      { label: "Copy Link", click: () => clipboard.writeText(params.linkURL) },
      { type: "separator" },
    );
  }

  if (params.isEditable) {
    items.push(
      { role: "cut", enabled: params.editFlags.canCut },
      { role: "copy", enabled: params.editFlags.canCopy },
      { role: "paste", enabled: params.editFlags.canPaste },
      { type: "separator" },
      { role: "selectAll" },
    );
  } else if (params.selectionText.trim()) {
    items.push({ role: "copy" });
  }

  while (items.at(-1)?.type === "separator") items.pop();
  if (items.length > 0) Menu.buildFromTemplate(items).popup({ window });
}
