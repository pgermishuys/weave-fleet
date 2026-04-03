"use client";

import { useState, useEffect, useCallback } from "react";
import { apiFetch } from "@/lib/api-client";

interface InstalledSkill {
  name: string;
  description: string;
  path: string;
  assignedAgents: string[];
}

export function useSkills() {
  const [skills, setSkills] = useState<InstalledSkill[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const fetchSkills = useCallback(async () => {
    try {
      setIsLoading(true);
      setError(null);
      const res = await apiFetch("/api/skills");
      if (!res.ok) {
        throw new Error(`Failed to fetch skills: ${res.status}`);
      }
      const json = await res.json();
      setSkills(json.skills ?? []);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unknown error");
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchSkills();
  }, [fetchSkills]);

  const installSkill = useCallback(
    async (options: { url?: string; content?: string; agents?: string[] }) => {
      try {
        setError(null);
        const res = await apiFetch("/api/skills", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(options),
        });
        if (!res.ok) {
          const json = await res.json().catch(() => ({}));
          throw new Error(json.error ?? `Failed to install skill: ${res.status}`);
        }
        await fetchSkills();
        return await res.json();
      } catch (err) {
        setError(err instanceof Error ? err.message : "Unknown error");
        throw err;
      }
    },
    [fetchSkills]
  );

  const removeSkill = useCallback(
    async (name: string) => {
      try {
        setError(null);
        const res = await apiFetch(`/api/skills/${encodeURIComponent(name)}`, {
          method: "DELETE",
        });
        if (!res.ok) {
          const json = await res.json().catch(() => ({}));
          throw new Error(json.error ?? `Failed to remove skill: ${res.status}`);
        }
        await fetchSkills();
      } catch (err) {
        setError(err instanceof Error ? err.message : "Unknown error");
        throw err;
      }
    },
    [fetchSkills]
  );

  return {
    skills,
    isLoading,
    error,
    fetchSkills,
    installSkill,
    removeSkill,
  };
}
