import { tool } from "@opencode-ai/plugin"

export default tool({
  description: `Render a visual diagram inline in the conversation.

Two rendering modes:

1. **sequence** (Mermaid): For sequence diagrams with participant lifelines and ordered messages.
   - content: Valid Mermaid DSL string. Do NOT include fenced code block markers.
   - Example:
     type: "sequence"
     content: "sequenceDiagram\\n  Agent->>Foundry: render diagram\\n  Foundry->>User: displays inline"

2. **flow** (Interactive graph): For all other diagrams: flowcharts, architecture, state machines, class diagrams, dependency graphs, etc.
   - content: JSON string with nodes and edges arrays. Positions are computed automatically via auto-layout.
   - direction: Optional layout direction in content object ("TB", "LR", "BT", "RL"). Defaults to "TB".
   - Nodes need only id and label. Optional: type ("input", "output", "default"), group (string).
   - Edges need id, source, target. Optional: label (string), animated (boolean).
   - Example:
     type: "flow"
     content: "{\\"nodes\\":[{\\"id\\":\\"1\\",\\"label\\":\\"API Gateway\\"},{\\"id\\":\\"2\\",\\"label\\":\\"Auth Service\\"},{\\"id\\":\\"3\\",\\"label\\":\\"Database\\"}],\\"edges\\":[{\\"id\\":\\"e1-2\\",\\"source\\":\\"1\\",\\"target\\":\\"2\\",\\"label\\":\\"validates\\"},{\\"id\\":\\"e2-3\\",\\"source\\":\\"2\\",\\"target\\":\\"3\\",\\"animated\\":true}],\\"direction\\":\\"TB\\"}"`,
  args: {
    type: tool.schema
      .enum(["sequence", "flow"])
      .describe("The type of diagram: 'sequence' for Mermaid sequence diagrams, 'flow' for interactive node-edge graphs (flowcharts, architecture, state machines, etc.)"),
    content: tool.schema
      .string()
      .describe("Diagram content. For 'sequence': Mermaid DSL string. For 'flow': JSON string with nodes and edges arrays."),
    title: tool.schema
      .string()
      .optional()
      .describe("Optional title displayed above the diagram"),
  },
  async execute(args) {
    let content: string | object = args.content
    // For flow type, parse the JSON string into an object so the frontend can consume it directly
    if (args.type === "flow") {
      try {
        content = JSON.parse(args.content)
      } catch {
        return JSON.stringify({ error: "Invalid JSON in flow content" })
      }
    }
    return JSON.stringify({
      $type: `visual/${args.type}`,
      content,
      title: args.title,
    })
  },
})
