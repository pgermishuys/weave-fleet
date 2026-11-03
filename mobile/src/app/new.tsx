// New session: the box, and under it the line that says where it runs (machine, folder, worktree). The defaults are
// filled in already, so typing and sending is all it takes; each part of the line opens a sheet.
import { router } from "expo-router";
import { useEffect, useMemo, useState } from "react";
import { KeyboardAvoidingView, Platform, Pressable, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import type { HarnessInfo, ScannedRepository } from "@fleet/api/client";
import { buildCreateSessionRequest, type NewSessionWorkspace } from "@fleet/lib/new-session-request";
import { manager, useFleet } from "~/fleet/hooks";
import { newSessionRecents } from "~/fleet/new-session";
import { blockerOf, chooseFolder, chooseHarness, chooseMachine, folderFor, folderLabel, readyHarnesses, recentFolders } from "~/fleet/new-session-core";
import { Composer, Icon, ModelChip, Notice, Panel, Text, WhereLine, type WherePart } from "~/kit";
import { FolderSheet, MachineSheet, ModelSheet, WorktreeSheet, type ModelPick } from "~/ui/new-session-sheets";
import { useTheme } from "~/theme";

type SheetName = "machine" | "folder" | "worktree" | "model" | null;
const leave = () => (router.canGoBack() ? router.back() : router.replace("/"));

export default function NewSession() {
  const { color } = useTheme();
  const insets = useSafeAreaInsets();
  const { machines, sections } = useFleet();
  const [recentsReady, setRecentsReady] = useState(false);
  const [machinePick, setMachinePick] = useState<string>();
  // Folder: undefined until picked (the default shows), null for a quick chat.
  const [folderPick, setFolderPick] = useState<string | null>();
  const [workspace, setWorkspace] = useState<NewSessionWorkspace>({ kind: "new" });
  const [harnessPick, setHarnessPick] = useState<string>();
  const [model, setModel] = useState<ModelPick | null>(null);
  const [text, setText] = useState(newSessionRecents.draft);
  const [sheet, setSheet] = useState<SheetName>(null);
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [repositories, setRepositories] = useState<ScannedRepository[]>([]);
  const [harnesses, setHarnesses] = useState<HarnessInfo[] | null>(null);

  useEffect(() => { void newSessionRecents.load(machines.map((m) => m.machineId)).finally(() => setRecentsReady(true)); }, [machines.length]); // eslint-disable-line react-hooks/exhaustive-deps

  // A machine picked here stays, offline or not (the notice says why it can't start); the default is chooseMachine's.
  const machine = recentsReady ? (machines.find((m) => m.machineId === machinePick) ?? chooseMachine(machines, newSessionRecents.machine())) : undefined;
  const machineId = machine?.machineId;
  const api = machineId ? manager.apiFor(machineId) : undefined;
  const connected = machine?.state === "connected";

  // What the machine has: its repositories and harnesses, read again when the machine changes or comes back.
  useEffect(() => {
    setRepositories([]);
    setHarnesses(null);
    if (!api || !connected) return;
    void api.repositories().then((answer) => setRepositories(answer.repositories)).catch(() => {});
    void api.harnesses().then(setHarnesses).catch(() => {});
  }, [api, connected]);

  const sessions = useMemo(() => sections.flatMap((section) => section.data).filter((entry) => entry.machineId === machineId).map((entry) => entry.item), [sections, machineId]);
  const phoneFolders = machineId ? newSessionRecents.folders(machineId) : [];
  const folderPath = folderPick !== undefined ? folderPick : machineId ? chooseFolder(phoneFolders, sessions) : undefined;
  const folder = folderPath ? folderFor(folderPath, repositories) : folderPath === null ? ({ kind: "none" } as const) : null;
  const isRepository = folder?.kind === "repository";
  const ready = harnesses ? readyHarnesses(harnesses) : [];
  const harness = harnesses ? (ready.find((h) => h.type === harnessPick) ?? chooseHarness(harnesses)) : undefined;

  const blocker = machine ? blockerOf({ machineName: machine.machineName, state: machine.state, otherMachines: machines.length > 1, harnessesLoaded: harnesses !== null, harnessReady: !!harness, hasFolder: folder !== null }) : null;
  const canStart = !!machine && !blocker && !!harness && folder !== null;

  const pickMachine = (id: string) => { setMachinePick(id); setFolderPick(undefined); setHarnessPick(undefined); setModel(null); setWorkspace({ kind: "new" }); setSheet(null); };
  const pickFolder = (next: ReturnType<typeof folderFor> | { kind: "none" }) => { setFolderPick(next.kind === "none" ? null : next.path); setWorkspace({ kind: "new" }); setModel(null); setSheet(null); };

  const send = async () => {
    if (!api || !machineId || !harness || !folder || sending) return;
    const built = buildCreateSessionRequest({ folder, workspace, message: text, harnessType: harness.type, ...(model ? { model: model.ref } : {}) });
    if (!built.ok) return setError(built.error);
    setSending(true);
    setError(null);
    try {
      const created = await api.createSession(built);
      newSessionRecents.draft = "";
      await newSessionRecents.remember(machineId, folderPath || undefined, harness.type, model?.ref ?? null).catch(() => {});
      manager.refresh().catch(() => {});
      router.replace(`/s/${machineId}/${created.session.id}`);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Fleet didn't start it.");
      setSending(false);
    }
  };

  const workspaceLabel = workspace.kind === "new" ? "New worktree" : workspace.kind === "current" ? "This checkout" : (workspace.path.split(/[\\/]/).filter(Boolean).pop() ?? "Worktree");
  const parts: WherePart[] = [
    ...(machines.length > 1 && machine ? [{ key: "machine", label: machine.machineName, ...(blocker?.part === "machine" ? { tone: blocker.tone } : {}) }] : []),
    { key: "folder", label: folder ? folderLabel(folder, repositories) : "Pick a folder", ...(folder === null ? { tone: "warning" as const } : {}) },
    ...(isRepository ? [{ key: "worktree", label: workspaceLabel }] : []),
  ];
  const chipLabel = !harnesses ? "Harness" : harness ? `${harness.displayName} · ${model?.name ?? "Default"}` : "No harness";
  const notice = error ? { tone: "error" as const, lead: "Couldn't start it.", rest: error } : blocker;

  return (
    <KeyboardAvoidingView behavior={Platform.OS === "ios" ? "padding" : "height"} style={{ flex: 1, paddingTop: insets.top + 4, paddingBottom: insets.bottom + 6, paddingHorizontal: 6 }}>
      <Panel style={{ flex: 1 }}>
        <View style={{ flexDirection: "row", alignItems: "center", borderBottomWidth: 1, borderColor: color.border, paddingRight: 12 }}>
          <Pressable testID="new-back" accessibilityRole="button" accessibilityLabel="Back" hitSlop={8} onPress={leave} style={{ width: 48, height: 48, alignItems: "center", justifyContent: "center" }}>
            <Icon name="back" size={20} color={color.text} />
          </Pressable>
          <Text size={16} weight="semibold" accessibilityRole="header">New session</Text>
        </View>
        <View style={{ flex: 1, justifyContent: "center", paddingHorizontal: 20, gap: 6 }}>
          <Text size={20} weight="semibold">What should we work on?</Text>
          <Text size={14} tone="muted">{folder === null ? "Pick the folder it should work in first." : "Type below. The line under the box says where it runs."}</Text>
        </View>
        <View style={{ paddingHorizontal: 10, paddingBottom: 4, gap: 8 }}>
          {notice ? <Notice testID="new-notice" tone={notice.tone} lead={notice.lead} rest={notice.rest} /> : null}
          <Composer
            value={text} onChangeText={(next) => { setText(next); newSessionRecents.draft = next; }} onSend={() => void send()} sending={sending}
            sendDisabled={!canStart} autoFocus placeholder="Describe the task"
            chip={<ModelChip label={chipLabel} {...(harnesses && !harness ? { tone: "warning" as const } : {})} onPress={() => setSheet("model")} />}
          />
          <WhereLine parts={parts} onPress={(key) => setSheet(key as SheetName)} />
        </View>
      </Panel>
      <MachineSheet open={sheet === "machine"} onClose={() => setSheet(null)} machines={machines} current={machineId ?? ""} onPick={pickMachine} />
      <FolderSheet open={sheet === "folder"} onClose={() => setSheet(null)} machineName={machine?.machineName ?? ""} recent={recentFolders(phoneFolders, sessions)} repositories={repositories} current={folder} onPick={pickFolder} />
      <WorktreeSheet open={sheet === "worktree"} onClose={() => setSheet(null)} api={api} folder={isRepository && folderPath ? { path: folderPath, name: folderLabel(folder, repositories) } : null} workspace={workspace} onPick={(next) => { setWorkspace(next); setSheet(null); }} />
      <ModelSheet
        open={sheet === "model"} onClose={() => setSheet(null)} api={api} harnesses={ready} harness={harness} directory={folderPath || undefined} model={model}
        last={(type) => (machineId && folderPath ? newSessionRecents.model(machineId, folderPath, type) : undefined)}
        onPick={(type, picked) => { setHarnessPick(type); setModel(picked); setSheet(null); }}
      />
    </KeyboardAvoidingView>
  );
}
