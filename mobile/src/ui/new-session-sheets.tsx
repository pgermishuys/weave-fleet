// The four sheets of the New session screen: machine, folder, where in the folder, and harness and model.
import { useEffect, useState } from "react";
import { View } from "react-native";
import type { HarnessCatalog, HarnessInfo, ModelReference, ScannedRepository, WorktreeInfo } from "@fleet/api/client";
import type { NewSessionFolder, NewSessionWorkspace } from "@fleet/lib/new-session-request";
import { folderFor, folderLabel } from "~/fleet/new-session-core";
import type { MachineApi } from "~/fleet/api";
import type { MachineView } from "~/fleet/manager";
import { OptionRow, SearchField, SectionHeader, Segments, Sheet, Text } from "~/kit";

const STATE_TEXT: Record<string, string> = { connected: "Connected", connecting: "Connecting", reconnecting: "Reconnecting", offline: "Offline", "signed-out": "Signed out" };
const nameOf = (path: string) => path.split(/[\\/]/).filter(Boolean).pop() ?? path;
const matches = (query: string, ...texts: string[]) => !query.trim() || texts.some((text) => text.toLowerCase().includes(query.trim().toLowerCase()));
const Hint = ({ children }: { children: string }) => <Text size={13} tone="muted" style={{ paddingHorizontal: 10, paddingVertical: 12 }}>{children}</Text>;

export function MachineSheet({ open, onClose, machines, current, onPick }: {
  open: boolean; onClose: () => void; machines: MachineView[]; current: string; onPick: (machineId: string) => void;
}) {
  return (
    <Sheet open={open} onClose={onClose} title="Machine" testID="sheet-machine">
      {machines.map((m) => (
        <OptionRow key={m.machineId} testID={`machine-${m.machineName}`} icon="machines" title={m.machineName} detail={STATE_TEXT[m.state] ?? m.state} selected={m.machineId === current} onPress={() => onPick(m.machineId)} />
      ))}
    </Sheet>
  );
}

/** Recent folders first (the phone's and the machine's newest sessions'), then every repository Fleet found there, then none. */
export function FolderSheet({ open, onClose, machineName, recent, repositories, current, onPick }: {
  open: boolean; onClose: () => void; machineName: string; recent: string[]; repositories: readonly ScannedRepository[];
  current: NewSessionFolder | null | undefined; onPick: (folder: NewSessionFolder) => void;
}) {
  const [query, setQuery] = useState("");
  useEffect(() => { if (!open) setQuery(""); }, [open]);
  const currentPath = current && current.kind !== "none" ? current.path : undefined;
  const row = (path: string, key: string) => (
    <OptionRow key={`${key}:${path}`} testID={`folder-${nameOf(path)}`} icon="folder" mono title={folderLabel(folderFor(path, repositories), repositories)} detail={path} selected={path === currentPath} onPress={() => onPick(folderFor(path, repositories))} />
  );
  const recents = recent.filter((path) => matches(query, nameOf(path), path));
  const all = repositories.filter((repository) => !recent.includes(repository.path) && matches(query, repository.name, repository.path));
  return (
    <Sheet open={open} onClose={onClose} title="Folder" testID="sheet-folder">
      <SearchField testID="folder-search" value={query} onChangeText={setQuery} placeholder={`Search folders on ${machineName}`} />
      {recents.length ? <SectionHeader label="Recent" /> : null}
      {recents.map((path) => row(path, "recent"))}
      {all.length ? <SectionHeader label="All folders" count={all.length} /> : null}
      {all.map((repository) => row(repository.path, "all"))}
      {matches(query, "no folder", "quick chat") ? (
        <View style={{ marginTop: 8 }}>
          <OptionRow testID="folder-none" icon="sessions" title="No folder" detail="A quick chat, no files" selected={current?.kind === "none"} onPress={() => onPick({ kind: "none" })} />
        </View>
      ) : null}
    </Sheet>
  );
}

/** New worktree, this checkout, or a worktree the repository already has: the desktop's Workspace picker. */
export function WorktreeSheet({ open, onClose, api, folder, workspace, onPick }: {
  open: boolean; onClose: () => void; api: MachineApi | undefined; folder: { path: string; name: string } | null;
  workspace: NewSessionWorkspace; onPick: (workspace: NewSessionWorkspace) => void;
}) {
  const [worktrees, setWorktrees] = useState<WorktreeInfo[] | null>(null);
  const path = folder?.path;
  useEffect(() => {
    if (!open || !api || !path) return;
    setWorktrees(null);
    void api.worktrees(path).then((answer) => setWorktrees(answer.worktrees)).catch(() => setWorktrees([]));
  }, [open, api, path]);
  const checkout = worktrees?.find((worktree) => worktree.path === path);
  const others = (worktrees ?? []).filter((worktree) => worktree.path !== path);
  return (
    <Sheet open={open} onClose={onClose} title={`Where in ${folder?.name ?? "the folder"}`} testID="sheet-worktree">
      <OptionRow testID="workspace-new" icon="branch" title="New worktree" detail="From the default branch. Your checkout stays as it is." selected={workspace.kind === "new"} onPress={() => onPick({ kind: "new" })} />
      <OptionRow testID="workspace-current" icon="folder" title="This checkout" detail={checkout?.branch ? `On ${checkout.branch}` : "The folder as it is"} selected={workspace.kind === "current"} onPress={() => onPick({ kind: "current" })} />
      {others.length ? <SectionHeader label="Worktrees" /> : null}
      {others.map((worktree) => (
        <OptionRow key={worktree.path} icon="branch" title={nameOf(worktree.path)} detail={worktree.branch ?? worktree.path} selected={workspace.kind === "existing" && workspace.path === worktree.path} onPress={() => onPick({ kind: "existing", path: worktree.path })} />
      ))}
    </Sheet>
  );
}

export interface ModelPick { ref: ModelReference; name: string }

/** The ready harnesses across the top; under them Default, the model last used in this folder, and every model. */
export function ModelSheet({ open, onClose, api, harnesses, harness, directory, model, last, onPick }: {
  open: boolean; onClose: () => void; api: MachineApi | undefined; harnesses: HarnessInfo[]; harness: HarnessInfo | undefined;
  directory: string | undefined; model: ModelPick | null; last: (harnessType: string) => ModelReference | undefined;
  onPick: (harnessType: string, model: ModelPick | null) => void;
}) {
  const [type, setType] = useState(harness?.type ?? "");
  const [catalog, setCatalog] = useState<HarnessCatalog | null>(null);
  const [query, setQuery] = useState("");
  useEffect(() => { if (open) { setType(harness?.type ?? ""); setQuery(""); } }, [open]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!open || !api || !type) return;
    setCatalog(null);
    void api.catalog(type, directory).then(setCatalog).catch(() => setCatalog({ supported: false, agents: [], providers: [], defaultAgent: null, defaultModel: null }));
  }, [open, api, type, directory]);

  const all = (catalog?.providers ?? []).flatMap((provider) => provider.models.map((m) => ({ ref: { providerID: provider.id, modelID: m.id }, name: m.name, provider: provider.name })));
  const nameFor = (ref: ModelReference) => all.find((m) => m.ref.providerID === ref.providerID && m.ref.modelID === ref.modelID)?.name ?? ref.modelID;
  const defaultName = catalog?.defaultModel ? nameFor(catalog.defaultModel) : undefined;
  const lastRef = last(type);
  const same = (a: ModelReference | undefined, b: ModelReference | undefined) => !!a && !!b && a.providerID === b.providerID && a.modelID === b.modelID;
  const onThis = harness?.type === type;
  const shown = all.filter((m) => matches(query, m.name, m.provider)).slice(0, 60);
  return (
    <Sheet open={open} onClose={onClose} title="Harness and model" testID="sheet-model">
      <Segments value={type} options={harnesses.map((h) => ({ value: h.type, label: h.displayName }))} onChange={setType} />
      <SectionHeader label="Model" />
      <OptionRow testID="model-default" title="Default" detail={defaultName ? `${defaultName}, as ${harnesses.find((h) => h.type === type)?.displayName ?? "the harness"} is set there` : "Whatever the harness is set to there"} selected={onThis && !model} onPress={() => onPick(type, null)} />
      {lastRef ? <OptionRow testID="model-last" title={nameFor(lastRef)} detail="Used last in this folder" selected={onThis && same(model?.ref, lastRef)} onPress={() => onPick(type, { ref: lastRef, name: nameFor(lastRef) })} /> : null}
      {catalog && !catalog.supported ? <Hint>This harness can't list its models before a session starts. It uses its default.</Hint> : null}
      {all.length > 8 ? <View style={{ marginVertical: 8 }}><SearchField testID="model-search" value={query} onChangeText={setQuery} placeholder={`Search ${all.length} models`} /></View> : null}
      {shown.filter((m) => !same(m.ref, lastRef)).map((m) => (
        <OptionRow key={`${m.ref.providerID}/${m.ref.modelID}`} title={m.name} detail={m.provider} selected={onThis && same(model?.ref, m.ref)} onPress={() => onPick(type, { ref: m.ref, name: m.name })} />
      ))}
      {!catalog ? <Hint>Loading models…</Hint> : null}
    </Sheet>
  );
}
