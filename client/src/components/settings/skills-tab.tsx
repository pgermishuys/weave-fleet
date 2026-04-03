"use client";

import { useState, useCallback } from "react";
import { Loader2, Package, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { SkillCard } from "./skill-card";
import { InstallSkillDialog } from "./install-skill-dialog";
import { useConfig } from "@/hooks/use-config";
import { useSkills } from "@/hooks/use-skills";

export function SkillsTab() {
  const { config, updateConfig } = useConfig();
  const { skills, isLoading, removeSkill, fetchSkills } = useSkills();
  const [installDialogOpen, setInstallDialogOpen] = useState(false);
  const [removingSkill, setRemovingSkill] = useState<string | null>(null);

  // Collect all agent names from config
  const allAgents = config?.agents ? Object.keys(config.agents) : [];

  const handleToggleAgent = useCallback(
    async (skillName: string, agentName: string, assigned: boolean) => {
      if (!config) return;

      const updatedConfig = { ...config };
      if (!updatedConfig.agents) {
        updatedConfig.agents = {};
      }

      if (!updatedConfig.agents[agentName]) {
        updatedConfig.agents[agentName] = { skills: [] };
      }

      const agentSkills = updatedConfig.agents[agentName].skills ?? [];

      if (assigned && !agentSkills.includes(skillName)) {
        updatedConfig.agents[agentName] = {
          ...updatedConfig.agents[agentName],
          skills: [...agentSkills, skillName],
        };
      } else if (!assigned) {
        updatedConfig.agents[agentName] = {
          ...updatedConfig.agents[agentName],
          skills: agentSkills.filter((s) => s !== skillName),
        };
      }

      await updateConfig(updatedConfig);
      // Refresh skills to get updated agent assignments
      await fetchSkills();
    },
    [config, updateConfig, fetchSkills]
  );

  const handleRemove = useCallback(
    async (name: string) => {
      if (
        !window.confirm(
          `Remove skill "${name}"? This will delete the skill files and remove it from all agent assignments.`
        )
      ) {
        return;
      }

      try {
        setRemovingSkill(name);
        await removeSkill(name);
      } finally {
        setRemovingSkill(null);
      }
    },
    [removeSkill]
  );

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-16">
        <Loader2 className="h-5 w-5 animate-spin text-muted-foreground" />
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {/* Header */}
      <div className="flex items-center justify-between">
        <p className="text-sm text-muted-foreground">
          {skills.length} skill{skills.length !== 1 ? "s" : ""} installed
        </p>
        <Button
          size="sm"
          variant="outline"
          className="gap-1.5"
          onClick={() => setInstallDialogOpen(true)}
        >
          <Plus className="h-3.5 w-3.5" />
          Install Skill
        </Button>
      </div>

      {/* Skills list */}
      {skills.length === 0 ? (
        <div className="flex flex-col items-center justify-center py-16 text-muted-foreground">
          <Package className="h-8 w-8 mb-3 opacity-40" />
          <p className="text-sm">No skills installed</p>
          <p className="text-xs mt-1">
            Install skills with the button above or via CLI:{" "}
            <code className="font-mono text-[10px] bg-muted px-1 py-0.5 rounded">
              weave-fleet skill install &lt;url&gt;
            </code>
          </p>
        </div>
      ) : (
        <div className="grid gap-3 sm:grid-cols-1 md:grid-cols-2 lg:grid-cols-3">
          {skills.map((skill) => (
            <SkillCard
              key={skill.name}
              name={skill.name}
              description={skill.description}
              path={skill.path}
              assignedAgents={skill.assignedAgents}
              allAgents={allAgents}
              onToggleAgent={handleToggleAgent}
              onRemove={handleRemove}
              isRemoving={removingSkill === skill.name}
            />
          ))}
        </div>
      )}

      <InstallSkillDialog
        open={installDialogOpen}
        onOpenChange={setInstallDialogOpen}
      />
    </div>
  );
}
