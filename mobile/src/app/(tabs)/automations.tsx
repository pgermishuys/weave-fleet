import { View } from "react-native";
import { EmptyState } from "~/kit";
import { Heading, PanelPage } from "~/ui/page";

export default function Automations() {
  return (
    <PanelPage>
      <Heading title="Automations" />
      <View style={{ flex: 1, justifyContent: "center" }}><EmptyState title="Automations are coming" body="See what ran while you were away. For now they live on the computer." /></View>
    </PanelPage>
  );
}
