"use client";

import { useEffect, useCallback, useState } from "react";
import { Loader2, FolderOpen, ExternalLink } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Switch } from "@/components/ui/switch";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { useConfig } from "@/hooks/use-config";
import { apiFetch } from "@/lib/api-client";
import { useUpdatePreferences } from "@/lib/update-preferences";

interface VersionInfo {
  version: string;
  latest: string | null;
  updateAvailable: boolean;
  checkedAt: string | null;
  channel?: "stable" | "dev";
}

export function AboutTab() {
  const { paths, isLoading: configLoading } = useConfig();
  const [updatePreferences, setUpdatePreferences] = useUpdatePreferences();
  const [versionInfo, setVersionInfo] = useState<VersionInfo | null>(null);
  const [versionLoading, setVersionLoading] = useState(true);

  const fetchVersion = useCallback(async (channel: string) => {
    try {
      setVersionLoading(true);
      const res = await apiFetch(`/api/version?channel=${channel}`);
      const data: VersionInfo = await res.json();
      setVersionInfo(data);
    } catch {
      // ignore fetch errors
    } finally {
      setVersionLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchVersion(updatePreferences.channel);
  }, [fetchVersion, updatePreferences.channel]);

  const isLoading = configLoading || versionLoading;

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-16">
        <Loader2 className="h-5 w-5 animate-spin text-muted-foreground" />
      </div>
    );
  }

  return (
    <div className="space-y-6 max-w-xl">
      {/* Version */}
      <Card>
        <CardContent className="p-4 space-y-3">
          <h4 className="text-sm font-semibold">Version</h4>
          <div className="space-y-2">
            <div className="flex items-center justify-between">
              <span className="text-sm text-muted-foreground">
                Weave Fleet
              </span>
              <div className="flex items-center gap-2">
                <code className="text-sm font-mono">
                  v{versionInfo?.version ?? "unknown"}
                </code>
                {versionInfo?.updateAvailable && (
                  <Badge
                    variant="secondary"
                    className="text-[10px] bg-blue-500/10 text-blue-600 dark:text-blue-400"
                  >
                    Update available: v{versionInfo.latest}
                  </Badge>
                )}
                {versionInfo?.channel === "dev" && (
                  <Badge variant="outline" className="text-[10px]">
                    Dev channel
                  </Badge>
                )}
              </div>
            </div>
          </div>
        </CardContent>
      </Card>

      {/* Updates */}
      <Card>
        <CardContent className="p-4 space-y-4">
          <h4 className="text-sm font-semibold">Updates</h4>

          <div className="space-y-3">
            <div className="flex items-center justify-between gap-4">
              <div>
                <p className="text-sm">Auto-update</p>
                <p className="text-xs text-muted-foreground">
                  Download in background and apply on next start
                </p>
              </div>
              <Switch
                checked={updatePreferences.autoUpdate}
                onCheckedChange={(checked) =>
                  setUpdatePreferences((prev) => ({
                    ...prev,
                    autoUpdate: checked,
                  }))
                }
              />
            </div>

            <div className="space-y-2">
              <p className="text-sm">Update Channel</p>
              <Select
                value={updatePreferences.channel}
                onValueChange={(value) =>
                  setUpdatePreferences((prev) => ({
                    ...prev,
                    channel: value === "dev" ? "dev" : "stable",
                  }))
                }
              >
                <SelectTrigger className="w-[220px]">
                  <SelectValue placeholder="Select channel" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="stable">stable</SelectItem>
                  <SelectItem value="dev">dev</SelectItem>
                </SelectContent>
              </Select>
              <p className="text-xs text-muted-foreground">
                The dev channel tracks `main` and may be less stable.
              </p>
            </div>

            <div className="text-xs text-muted-foreground">
              Mode: {updatePreferences.autoUpdate ? "auto-download" : "manual"} ·
              Channel: {updatePreferences.channel}
            </div>
          </div>
        </CardContent>
      </Card>

      {/* Config Paths */}
      <Card>
        <CardContent className="p-4 space-y-3">
          <h4 className="text-sm font-semibold">Configuration Files</h4>
          <div className="space-y-2">
            <div className="flex items-start gap-2">
              <FolderOpen className="h-3.5 w-3.5 mt-0.5 text-muted-foreground shrink-0" />
              <div className="min-w-0">
                <p className="text-xs text-muted-foreground">User Config</p>
                <code className="text-xs font-mono break-all">
                  {paths?.userConfig ?? "~/.config/opencode/weave-opencode.jsonc"}
                </code>
              </div>
            </div>
            <div className="flex items-start gap-2">
              <FolderOpen className="h-3.5 w-3.5 mt-0.5 text-muted-foreground shrink-0" />
              <div className="min-w-0">
                <p className="text-xs text-muted-foreground">Skills Directory</p>
                <code className="text-xs font-mono break-all">
                  {paths?.skillsDir ?? "~/.config/opencode/skills/"}
                </code>
              </div>
            </div>
          </div>
        </CardContent>
      </Card>

      {/* Links */}
      <Card>
        <CardContent className="p-4 space-y-3">
          <h4 className="text-sm font-semibold">Links</h4>
          <div className="space-y-2">
            <a
              href="https://github.com/pgermishuys/weave-agent-fleet"
              target="_blank"
              rel="noopener noreferrer"
              className="flex items-center gap-2 text-sm text-blue-600 dark:text-blue-400 hover:text-blue-500 dark:hover:text-blue-300 transition-colors"
            >
              <ExternalLink className="h-3.5 w-3.5" />
              GitHub Repository
            </a>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}
