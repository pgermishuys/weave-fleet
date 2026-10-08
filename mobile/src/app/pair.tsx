// Pairing: the same one-time code the desktop's Settings → Machines → Add a phone shows, typed or scanned.
import { CameraView, useCameraPermissions } from "expo-camera";
import * as Device from "expo-device";
import { router } from "expo-router";
import { useState } from "react";
import { KeyboardAvoidingView, Platform, TextInput, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { Button, Panel, T } from "~/components/ui";
import { parsePairingLink, previewPairing, redeemPairing, type PairingTarget } from "~/fleet/api";
import { saveCredentials } from "~/fleet/credentials";
import { font, radius, usePalette } from "~/fleet/theme";

export default function Pair() {
  const p = usePalette();
  const insets = useSafeAreaInsets();
  const [address, setAddress] = useState("");
  const [code, setCode] = useState("");
  const [target, setTarget] = useState<PairingTarget | null>(null);
  const [machine, setMachine] = useState<string | null>(null);
  const [deviceName, setDeviceName] = useState(Device.deviceName ?? Device.modelName ?? "Phone");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [scanning, setScanning] = useState(false);
  const [permission, requestPermission] = useCameraPermissions();

  const input = { color: p.text, fontFamily: font.regular, fontSize: 16, backgroundColor: p.card, borderRadius: radius.button, borderWidth: 1, borderColor: p.border, paddingHorizontal: 14, minHeight: 48 };

  async function check(next: PairingTarget) {
    setBusy(true);
    setError(null);
    try {
      const preview = await previewPairing(next);
      setTarget(next);
      setMachine(preview.machineName);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  async function connect() {
    if (!target) return;
    setBusy(true);
    try {
      await saveCredentials(await redeemPairing(target, deviceName.trim() || "Phone", Platform.OS === "ios" || Platform.OS === "android" ? Platform.OS : "other"));
      router.replace("/");
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setBusy(false);
    }
  }

  if (scanning) {
    return (
      <CameraView
        style={{ flex: 1 }}
        barcodeScannerSettings={{ barcodeTypes: ["qr"] }}
        onBarcodeScanned={({ data }) => {
          const scanned = parsePairingLink(data);
          if (!scanned) return;
          setScanning(false);
          void check(scanned);
        }}
      />
    );
  }

  return (
    <KeyboardAvoidingView behavior={Platform.OS === "ios" ? "padding" : undefined} style={{ flex: 1, paddingTop: insets.top + 8, paddingBottom: insets.bottom + 8, paddingHorizontal: 8 }}>
      <Panel style={{ padding: 20, gap: 14 }}>
        {machine ? (
          <>
            <T weight="semibold" size={24}>Connect this phone to {machine}?</T>
            <T muted>The phone gets its own key. You can remove it any time in Settings → Machines on the computer.</T>
            <T muted size={13}>Name for this phone</T>
            <TextInput value={deviceName} onChangeText={setDeviceName} style={input} placeholderTextColor={p.muted} />
            <Button kind="primary" label={busy ? "Connecting…" : "Connect"} disabled={busy} onPress={() => void connect()} />
            <Button label="Back" onPress={() => { setMachine(null); setTarget(null); }} />
          </>
        ) : (
          <>
            <T weight="semibold" size={28}>Pair with Fleet</T>
            <T muted>On the computer: Settings → Machines → This machine → Add a phone. Scan the code, or type the address and the code under it.</T>
            {Platform.OS !== "web" && (
              <Button kind="primary" label="Scan the QR code" onPress={async () => { if (!permission?.granted) await requestPermission(); setScanning(true); }} />
            )}
            <T muted size={13}>Fleet's address</T>
            <TextInput testID="address" value={address} onChangeText={setAddress} placeholder="https://hangar.tailnet.ts.net" placeholderTextColor={p.muted} autoCapitalize="none" autoCorrect={false} keyboardType="url" style={input} />
            <T muted size={13}>Code</T>
            <TextInput testID="code" value={code} onChangeText={setCode} placeholder="XXXX-XXXX" placeholderTextColor={p.muted} autoCapitalize="characters" autoCorrect={false} style={[input, { fontFamily: font.mono, letterSpacing: 2 }]} />
            <Button
              label={busy ? "Checking…" : "Continue"}
              disabled={busy || !address.trim() || !code.trim()}
              onPress={() => {
                const pasted = parsePairingLink(address);
                void check(pasted ?? { baseUrl: address.trim().replace(/\/$/, ""), manualCode: code.trim() });
              }}
            />
          </>
        )}
        {error && <T style={{ color: p.error }}>{error}</T>}
        <View style={{ flex: 1 }} />
      </Panel>
    </KeyboardAvoidingView>
  );
}
