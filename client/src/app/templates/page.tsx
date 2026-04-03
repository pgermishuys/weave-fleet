"use client";

import { Header } from "@/components/layout/header";
import { mockTemplates } from "@/lib/mock-data";
import { Card, CardContent, CardHeader } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Play, Variable } from "lucide-react";

export default function TemplatesPage() {
  return (
    <div className="flex flex-col h-full">
      <Header
        title="Templates"
        subtitle={`${mockTemplates.length} task templates`}
      />
      <div className="flex-1 overflow-auto p-6">
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {mockTemplates.map((tpl) => (
            <Card key={tpl.id} className="flex flex-col">
              <CardHeader className="pb-2">
                <div className="flex items-start justify-between">
                  <h3 className="font-semibold text-sm">{tpl.name}</h3>
                  <Badge variant="secondary" className="text-[10px]">
                    Used {tpl.usageCount}x
                  </Badge>
                </div>
                <p className="text-xs text-muted-foreground">{tpl.description}</p>
              </CardHeader>
              <CardContent className="flex-1 space-y-3">
                {/* Prompt preview */}
                <div className="rounded-md bg-accent/50 p-2">
                  <p className="text-xs font-mono text-muted-foreground line-clamp-3">
                    {tpl.prompt}
                  </p>
                </div>

                {/* Variables */}
                {tpl.variables.length > 0 && (
                  <div className="space-y-1">
                    <span className="text-[10px] font-semibold uppercase text-muted-foreground">
                      Variables
                    </span>
                    <div className="flex flex-wrap gap-1">
                      {tpl.variables.map((v) => (
                        <Badge key={v.name} variant="outline" className="text-[10px] font-mono gap-1 px-1.5 py-0">
                          <Variable className="h-2.5 w-2.5" />
                          {`{{${v.name}}}`}
                          {v.required && <span className="text-red-500">*</span>}
                        </Badge>
                      ))}
                    </div>
                  </div>
                )}

                {/* Tags */}
                <div className="flex flex-wrap gap-1">
                  {tpl.tags.map((tag) => (
                    <Badge key={tag} variant="outline" className="text-[10px] px-1 py-0 text-muted-foreground">
                      {tag}
                    </Badge>
                  ))}
                </div>

                {/* Launch button */}
                <Button variant="outline" size="sm" className="w-full gap-1.5 mt-2">
                  <Play className="h-3 w-3" /> Launch Session
                </Button>
              </CardContent>
            </Card>
          ))}
        </div>
      </div>
    </div>
  );
}
