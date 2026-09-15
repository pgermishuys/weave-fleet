import { Menu, clipboard, shell, type BrowserWindow, type ContextMenuParams, type MenuItemConstructorOptions } from "electron";

export interface MenuActions {
  openLogs: () => void;
  openDataFolder: () => void;
  checkForUpdates: () => void;
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
    { role: "editMenu" },
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
