// Pairing: scan the QR code the computer shows, or type its address and the eight-character code. Also how a second
// machine is added.
import { CameraView, useCameraPermissions } from "expo-camera";
import * as Device from "expo-device";
import { router } from "expo-router";
import { useRef, useState } from "react";
import { KeyboardAvoidingView, Platform, Pressable, ScrollView, TextInput, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { machines, useMachines } from "~/fleet/machines";
import { previewPairing, redeemPairing, targetFromScan, targetFromTyped, type PairingTarget } from "~/fleet/pairing";
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
      setMachineName((await previewPairing(next)).machineName);
      setTarget(next);
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
      if (!next) throw new Error("That isn't a Fleet pairing code. On the computer, Settings → Machines → Add a phone shows one.");
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
            <Text tone="muted">This phone gets its own key. The computer can remove it any time in Settings → Machines.</Text>
            {error && <Text tone="error">{error}</Text>}
            <Button label="Connect" variant="primary" loading={busy} onPress={() => void connect()} testID="connect" />
            <Button label="Back" variant="ghost" onPress={() => setTarget(null)} />
          </>
        ) : (
          <>
            <Text size={28} weight="bold">Pair with Fleet</Text>
            <Text tone="muted">On the computer, open Settings → Machines → Add a phone.</Text>
            <Button label="Scan the QR code" variant="primary" icon="qr" onPress={() => void startScan()} />
            <Card>
              <View style={{ gap: 12 }}>
                <Text weight="semibold">Or type it</Text>
                <TextInput testID="address" value={address} onChangeText={setAddress} placeholder="https://your-computer.example" placeholderTextColor={color.muted} autoCapitalize="none" autoCorrect={false} keyboardType="url" style={input} />
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
