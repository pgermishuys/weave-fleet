import { View } from "react-native";
import { EmptyState } from "~/kit";
import { Heading, PanelPage } from "~/ui/page";

export default function Workflows() {
  return (
    <PanelPage>
      <Heading title="Workflows" />
      <View style={{ flex: 1, justifyContent: "center" }}><EmptyState title="Workflows are coming" body="Run and watch your workflows from here. For now they live on the computer." /></View>
    </PanelPage>
  );
}
