"use client";

import { Loader2, Users } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import {
  Select,
  SelectTrigger,
  SelectContent,
  SelectItem,
  SelectValue,
  SelectGroup,
  SelectLabel,
} from "@/components/ui/select";
import { useConfig } from "@/hooks/use-config";

export function AgentsTab() {
  const { config, installedSkills, providers, isLoading, updateConfig } =
    useConfig();

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-16">
        <Loader2 className="h-5 w-5 animate-spin text-muted-foreground" />
      </div>
    );
  }

  const agents = config?.agents ?? {};
  const agentNames = Object.keys(agents);
  const connectedProviders = providers.filter((p) => p.connected);

  const onModelChange = async (agentName: string, model: string) => {
    const currentAgents = config?.agents ?? {};
    const updatedAgents = {
      ...currentAgents,
      [agentName]: {
        ...currentAgents[agentName],
        model: model || undefined, // clear if empty
      },
    };
    await updateConfig({ agents: updatedAgents });
  };

  if (agentNames.length === 0) {
    return (
      <div className="flex flex-col items-center justify-center py-16 text-muted-foreground">
        <Users className="h-8 w-8 mb-3 opacity-40" />
        <p className="text-sm">No agent configurations found</p>
        <p className="text-xs mt-1">
          Agent mappings are defined in{" "}
          <code className="font-mono text-[10px] bg-muted px-1 py-0.5 rounded">
            weave-opencode.jsonc
          </code>
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <p className="text-sm text-muted-foreground">
        {agentNames.length} agent{agentNames.length !== 1 ? "s" : ""} configured.
        Showing skills and model assignments for each agent.
      </p>

      <div className="grid gap-4 sm:grid-cols-1 md:grid-cols-2">
        {agentNames.sort().map((agentName) => {
          const agentConfig = agents[agentName];
          const skills = agentConfig.skills ?? [];
          const currentModel = agentConfig.model ?? "";

          return (
            <Card key={agentName}>
              <CardContent className="p-4 space-y-3">
                <div className="flex items-center justify-between">
                  <h4 className="text-sm font-semibold font-mono">
                    {agentName}
                  </h4>
                  <Badge variant="secondary" className="text-[10px]">
                    {skills.length} skill{skills.length !== 1 ? "s" : ""}
                  </Badge>
                </div>

                {skills.length === 0 ? (
                  <p className="text-xs text-muted-foreground">
                    No skills assigned
                  </p>
                ) : (
                  <div className="flex flex-wrap gap-1.5">
                    {skills.map((skillName) => {
                      // Find description from installed skills
                      const installed = installedSkills.find(
                        (s) => s.name === skillName
                      );
                      return (
                        <Badge
                          key={skillName}
                          variant="outline"
                          className="text-[10px]"
                          title={installed?.description ?? ""}
                        >
                          {skillName}
                        </Badge>
                      );
                    })}
                  </div>
                )}

                {/* Model selection */}
                <div className="space-y-1.5 pt-1 border-t border-border/50">
                  <label className="text-xs text-muted-foreground font-medium">
                    Model
                  </label>
                  {connectedProviders.length === 0 ? (
                    <p className="text-xs text-muted-foreground italic">
                      Connect a provider in the Providers tab to select models.
                    </p>
                  ) : (
                    <Select
                      value={currentModel}
                      onValueChange={(value) =>
                        onModelChange(agentName, value === "__default__" ? "" : value)
                      }
                    >
                      <SelectTrigger className="h-8 text-xs">
                        <SelectValue placeholder="Default (no override)" />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectItem value="__default__" className="text-xs">
                          Default (no override)
                        </SelectItem>
                        {connectedProviders.map((provider) => (
                          <SelectGroup key={provider.id}>
                            <SelectLabel>{provider.name}</SelectLabel>
                            {provider.models.map((model) => (
                              <SelectItem
                                key={`${provider.id}/${model.id}`}
                                value={`${provider.id}/${model.id}`}
                                className="text-xs"
                              >
                                {model.name}
                              </SelectItem>
                            ))}
                          </SelectGroup>
                        ))}
                      </SelectContent>
                    </Select>
                  )}
                </div>
              </CardContent>
            </Card>
          );
        })}
      </div>
    </div>
  );
}
