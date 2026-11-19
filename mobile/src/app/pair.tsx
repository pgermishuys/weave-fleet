// Pairing: scan the QR code the machine shows, or type its address and the eight-character code. Also how a second
// machine is added.
import { CameraView, useCameraPermissions } from "expo-camera";
import * as Device from "expo-device";
import { router } from "expo-router";
import { useRef, useState } from "react";
import { KeyboardAvoidingView, Platform, Pressable, ScrollView, TextInput, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { machines, useMachines } from "~/fleet/machines";
import { previewPairing, redeemPairing, targetFromScan, targetFromTyped, type PairingTarget } from "~/fleet/pairing";
import { isTailscale, kindOf } from "~/fleet/routes-core";
import { Button, Card, Icon, Text } from "~/kit";
import { useTheme } from "~/theme";

export default function Pair() {
  const { color, font, radius } = useTheme();
  const insets = useSafeAreaInsets();
  const paired = useMachines();
  const [address, setAddress] = useState("");
  const [code, setCode] = useState("");
  const [scanning, setScanning] = useState(false);
  const [target, setTarget] = useState<PairingTarget | null>(null);
  const [machineName, setMachineName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [permission, askPermission] = useCameraPermissions();
  const scanned = useRef(false);

  async function preview(next: PairingTarget) {
    setBusy(true);
    setError(null);
    try {
      const found = await previewPairing(next);
      setMachineName(found.machineName);
      setTarget(found.target);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
      scanned.current = false;
    }
  }

  async function connect() {
    if (!target) return;
    setBusy(true);
    setError(null);
    try {
      await machines.add(await redeemPairing(target, Device.deviceName ?? Device.modelName ?? "Phone", Platform.OS));
      router.replace("/");
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setBusy(false);
    }
  }

  function onTyped() {
    try {
      void preview(targetFromTyped(address, code));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  async function startScan() {
    if (!permission?.granted && !(await askPermission()).granted) {
      setError("Fleet needs the camera to scan the code. You can type the address and code instead.");
      return;
    }
    setError(null);
    setScanning(true);
  }

  function onScanned(text: string) {
    if (scanned.current) return;
    scanned.current = true;
    setScanning(false);
    try {
      const next = targetFromScan(text);
      if (!next) throw new Error("That isn't a Fleet pairing code. On the machine, Add your phone in Settings → Machines shows one; on a machine with no screen, run fleet node pair.");
      void preview(next);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      scanned.current = false;
    }
  }

  const input = {
    height: 48, borderRadius: radius.button, borderWidth: 1, borderColor: color.border, backgroundColor: color.card,
    color: color.text, fontFamily: font.regular, fontSize: 16, paddingHorizontal: 14,
  } as const;

  if (scanning) {
    return (
      <View style={{ flex: 1, backgroundColor: "#000" }}>
        <CameraView style={{ flex: 1 }} facing="back" barcodeScannerSettings={{ barcodeTypes: ["qr"] }} onBarcodeScanned={({ data }) => onScanned(data)} />
        <View style={{ position: "absolute", left: 16, right: 16, bottom: insets.bottom + 16 }}>
          <Button label="Cancel" variant="secondary" onPress={() => setScanning(false)} />
        </View>
      </View>
    );
  }

  return (
    <KeyboardAvoidingView behavior={Platform.OS === "ios" ? "padding" : undefined} style={{ flex: 1 }}>
      <ScrollView keyboardShouldPersistTaps="handled" contentContainerStyle={{ padding: 20, paddingTop: insets.top + 16, paddingBottom: insets.bottom + 24, gap: 16 }}>
        {paired.length > 0 && (
          <Pressable accessibilityLabel="Back" hitSlop={12} onPress={() => router.back()} style={{ width: 40, height: 40, justifyContent: "center" }}>
            <Icon name="back" size={22} color={color.text} />
          </Pressable>
        )}
        {target ? (
          <>
            <Text size={24} weight="bold">Connect this phone to {machineName}?</Text>
            <Text tone="muted">{target.baseUrl}</Text>
            <Text tone="muted">This phone gets its own key for this machine. Remove it any time from Settings → Machines on the machine.</Text>
            {(target.addresses?.length ?? 0) > 1 && (
              <View style={{ gap: 4 }} testID="reach">
                {target.addresses!.some((url) => ["lan", "hostname"].includes(kindOf(url))) && <Text tone="muted">At home, on your Wi-Fi</Text>}
                {target.addresses!.some((url) => isTailscale(kindOf(url))) && <Text tone="muted">Away from home, with Tailscale on the phone</Text>}
              </View>
            )}
            {error && <Text tone="error">{error}</Text>}
            <Button label="Connect" variant="primary" loading={busy} onPress={() => void connect()} testID="connect" />
            <Button label="Back" variant="ghost" onPress={() => setTarget(null)} />
          </>
        ) : (
          <>
            <Text size={28} weight="bold">Connect to your Fleet</Text>
            <Text tone="muted">On the machine, open Fleet and choose Add your phone. It is in Settings → Machines. On a machine with no screen, run fleet node pair.</Text>
            <Button label="Scan the code" variant="primary" icon="qr" onPress={() => void startScan()} />
            <Card>
              <View style={{ gap: 12 }}>
                <Text weight="semibold">Or type the address and code</Text>
                <TextInput testID="address" value={address} onChangeText={setAddress} placeholder="https://your-machine.example" placeholderTextColor={color.muted} autoCapitalize="none" autoCorrect={false} keyboardType="url" style={input} />
                <TextInput testID="code" value={code} onChangeText={setCode} placeholder="K7QM-4T2X" placeholderTextColor={color.muted} autoCapitalize="characters" autoCorrect={false} style={[input, { fontFamily: font.mono }]} />
                <Button label="Continue" loading={busy} disabled={!address.trim() || !code.trim()} onPress={onTyped} testID="continue" />
              </View>
            </Card>
            {error && <Text tone="error">{error}</Text>}
          </>
        )}
      </ScrollView>
    </KeyboardAvoidingView>
  );
}
