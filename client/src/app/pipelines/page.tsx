"use client";

import { Header } from "@/components/layout/header";
import { mockPipelines, getStatusColor } from "@/lib/mock-data";
import { Card, CardContent, CardHeader } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { ArrowRight, GitBranch } from "lucide-react";

function getStageStatusBadge(status: string) {
  switch (status) {
    case "completed":
      return <Badge variant="secondary" className="bg-green-500/10 text-green-500 text-[10px]">Done</Badge>;
    case "running":
      return <Badge variant="secondary" className="bg-blue-500/10 text-blue-500 text-[10px]">Running</Badge>;
    case "pending":
      return <Badge variant="outline" className="text-[10px]">Pending</Badge>;
    case "failed":
      return <Badge variant="secondary" className="bg-red-500/10 text-red-500 text-[10px]">Failed</Badge>;
    default:
      return <Badge variant="outline" className="text-[10px]">{status}</Badge>;
  }
}

export default function PipelinesPage() {
  return (
    <div className="flex flex-col h-full">
      <Header title="Pipelines" subtitle={`${mockPipelines.length} pipelines`} />
      <div className="flex-1 overflow-auto p-6 space-y-6">
        {mockPipelines.map((pipeline) => (
          <Card key={pipeline.id}>
            <CardHeader className="pb-3">
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <GitBranch className={`h-4 w-4 ${getStatusColor(pipeline.status)}`} />
                  <h3 className="font-semibold">{pipeline.name}</h3>
                  <Badge variant="outline" className="text-xs capitalize">
                    {pipeline.status}
                  </Badge>
                </div>
              </div>
              <p className="text-xs text-muted-foreground">{pipeline.description}</p>
            </CardHeader>
            <CardContent>
              <div className="flex items-center gap-2 flex-wrap">
                {pipeline.stages.map((stage, i) => (
                  <div key={stage.id} className="flex items-center gap-2">
                    <div className="flex flex-col items-center gap-1 rounded-lg border p-3 min-w-[140px]">
                      <span className="text-xs font-medium">{stage.name}</span>
                      {getStageStatusBadge(stage.status)}
                      {stage.tokens != null && (
                        <span className="text-[10px] text-muted-foreground">
                          {(stage.tokens / 1000).toFixed(1)}k tokens
                        </span>
                      )}
                    </div>
                    {i < pipeline.stages.length - 1 && (
                      <ArrowRight className="h-4 w-4 text-muted-foreground shrink-0" />
                    )}
                  </div>
                ))}
              </div>
            </CardContent>
          </Card>
        ))}
      </div>
    </div>
  );
}
