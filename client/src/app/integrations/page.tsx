"use client";

import { Suspense } from "react";
import { Header } from "@/components/layout/header";
import { Tabs, TabsList, TabsTrigger, TabsContent } from "@/components/ui/tabs";
import { useIntegrationsContext } from "@/contexts/integrations-context";
import { getIntegrations } from "@/integrations/registry";

export default function IntegrationsPage() {
  const { connectedIntegrations } = useIntegrationsContext();
  const allManifests = getIntegrations();

  // Filter manifests to only connected ones
  const connectedManifests = allManifests.filter((m) =>
    connectedIntegrations.some((i) => i.id === m.id)
  );

  const defaultTab = connectedManifests[0]?.id;

  return (
    <div className="flex flex-col h-full">
      <Header title="Integrations" subtitle="Browse connected integrations" />
      <div className="flex-1 overflow-auto p-6">
        {connectedManifests.length === 0 ? (
          <div className="flex flex-col items-center justify-center h-full text-center gap-3 py-12">
            <p className="text-sm text-muted-foreground">
              No integrations connected.
            </p>
            <p className="text-xs text-muted-foreground">
              Go to{" "}
              <a href="/settings?tab=integrations" className="underline">
                Settings &rsaquo; Integrations
              </a>{" "}
              to connect one.
            </p>
          </div>
        ) : (
          <Tabs defaultValue={defaultTab}>
            <TabsList variant="line">
              {connectedManifests.map((manifest) => {
                const Icon = manifest.icon;
                return (
                  <TabsTrigger
                    key={manifest.id}
                    value={manifest.id}
                    className="gap-1.5"
                  >
                    <Icon size={14} />
                    {manifest.name}
                  </TabsTrigger>
                );
              })}
            </TabsList>
            {connectedManifests.map((manifest) => {
              const BrowserComponent = manifest.browserComponent;
              return (
                <TabsContent
                  key={manifest.id}
                  value={manifest.id}
                  className="mt-4"
                >
                  <Suspense fallback={null}>
                    <BrowserComponent />
                  </Suspense>
                </TabsContent>
              );
            })}
          </Tabs>
        )}
      </div>
    </div>
  );
}
