namespace NuCode.Providers;

/// <summary>
/// Static catalog of all built-in LLM provider definitions supported by NuCode.
/// Covers the full OpenCode provider directory plus the existing NuCode providers.
/// </summary>
public static class BuiltInProviders
{
    private static readonly CredentialField ApiKeyField = new(
        Key: "apiKey",
        DisplayName: "API Key",
        Required: true,
        IsSecret: true);

    private static readonly CredentialField OptionalApiKeyField = new(
        Key: "apiKey",
        DisplayName: "API Key",
        Required: false,
        IsSecret: true,
        HelpText: "Leave empty for local models that don't require authentication.");

    /// <summary>Returns all built-in provider definitions.</summary>
    public static IReadOnlyList<ProviderDefinition> All() =>
    [
        // ── OAuth Device Flow ──────────────────────────────────────────────────
        new()
        {
            Id = "copilot",
            DisplayName = "GitHub Copilot",
            Description = "Use your GitHub Copilot subscription. Requires an active Copilot plan.",
            AuthMechanism = AuthMechanism.OAuthDevice,
            CredentialFields =
            [
                new("githubToken", "GitHub OAuth Token", Required: true, IsSecret: true,
                    HelpText: "Obtained automatically via the GitHub device flow.")
            ],
            DefaultEndpoint = "https://api.githubcopilot.com/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },

        // ── API Key providers ──────────────────────────────────────────────────
        new()
        {
            Id = "anthropic",
            DisplayName = "Anthropic",
            Description = "Claude models from Anthropic.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.anthropic.com/v1/",
            SupportsCustomBaseUrl = true,
            IsOpenAiCompatible = true,
            ModelPrefixes = ["claude"],
        },
        new()
        {
            Id = "openai",
            DisplayName = "OpenAI",
            Description = "GPT and o-series models from OpenAI.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = null, // SDK uses default
            SupportsCustomBaseUrl = true,
            IsOpenAiCompatible = true,
            ModelPrefixes = ["gpt", "o1", "o3", "o4", "o2"],
        },
        new()
        {
            Id = "openrouter",
            DisplayName = "OpenRouter",
            Description = "Access 40+ models through a single API.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://openrouter.ai/api/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "groq",
            DisplayName = "Groq",
            Description = "Ultra-fast inference for open models.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.groq.com/openai/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "deepseek",
            DisplayName = "DeepSeek",
            Description = "DeepSeek V4 Pro and other DeepSeek models.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.deepseek.com/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = ["deepseek"],
        },
        new()
        {
            Id = "cerebras",
            DisplayName = "Cerebras",
            Description = "High-speed inference including Qwen 3 Coder 480B.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.cerebras.ai/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "fireworks",
            DisplayName = "Fireworks AI",
            Description = "Fast inference for open-source models.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.fireworks.ai/inference/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "together",
            DisplayName = "Together AI",
            Description = "Run and fine-tune open-source AI models.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.together.xyz/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "xai",
            DisplayName = "xAI",
            Description = "Grok models from xAI.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.x.ai/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = ["grok"],
        },
        new()
        {
            Id = "mistral",
            DisplayName = "Mistral AI",
            Description = "Mistral and Mixtral models.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.mistral.ai/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = ["mistral", "mixtral"],
        },
        new()
        {
            Id = "moonshot",
            DisplayName = "Moonshot AI",
            Description = "Kimi K2 and other Moonshot models.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.moonshot.ai/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = ["moonshot", "kimi"],
        },
        new()
        {
            Id = "minimax",
            DisplayName = "MiniMax",
            Description = "MiniMax M2.1 and other models.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.minimax.chat/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "nvidia",
            DisplayName = "NVIDIA",
            Description = "Nemotron and other models via build.nvidia.com.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://integrate.api.nvidia.com/v1/",
            SupportsCustomBaseUrl = true,
            IsOpenAiCompatible = true,
            ModelPrefixes = ["nemotron"],
        },
        new()
        {
            Id = "huggingface",
            DisplayName = "Hugging Face",
            Description = "Open models via Hugging Face Inference Providers.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api-inference.huggingface.co/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "opencode-zen",
            DisplayName = "OpenCode Zen",
            Description = "Curated models tested and verified by the OpenCode team.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.opencode.ai/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "opencode-go",
            DisplayName = "OpenCode Go",
            Description = "Low-cost subscription plan from the OpenCode team.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.opencode.ai/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "302ai",
            DisplayName = "302.AI",
            Description = "Aggregated access to many AI models.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.302.ai/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "baseten",
            DisplayName = "Baseten",
            Description = "Deploy and serve ML models at scale.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://inference.baseten.co/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "cortecs",
            DisplayName = "Cortecs",
            Description = "Fast inference including Kimi K2 Instruct.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.cortecs.ai/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "deep-infra",
            DisplayName = "Deep Infra",
            Description = "Affordable inference for open-source models.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.deepinfra.com/v1/openai/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "frogbot",
            DisplayName = "FrogBot",
            Description = "AI models via FrogBot.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.frogbot.ai/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "helicone",
            DisplayName = "Helicone",
            Description = "LLM observability gateway with logging and analytics.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://ai-gateway.helicone.ai/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "ionet",
            DisplayName = "IO.NET",
            Description = "Decentralized GPU cloud for AI inference.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.io.net/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "nebius",
            DisplayName = "Nebius Token Factory",
            Description = "Fast inference including Kimi K2 Instruct.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.studio.nebius.com/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "ollama-cloud",
            DisplayName = "Ollama Cloud",
            Description = "Cloud-hosted models via Ollama.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.ollama.com/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "llmgateway",
            DisplayName = "LLM Gateway",
            Description = "Unified gateway for multiple LLM providers.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.llmgateway.io/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "venice",
            DisplayName = "Venice AI",
            Description = "Privacy-preserving AI inference.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.venice.ai/api/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "vercel",
            DisplayName = "Vercel AI Gateway",
            Description = "AI gateway from Vercel.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://ai-gateway.vercel.sh/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "zai",
            DisplayName = "Z.AI",
            Description = "AI models via Z.AI.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.z.ai/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "zenmux",
            DisplayName = "ZenMux",
            Description = "AI model multiplexer.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://api.zenmux.com/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "ovhcloud",
            DisplayName = "OVHcloud AI Endpoints",
            Description = "AI inference on OVHcloud infrastructure.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [ApiKeyField],
            DefaultEndpoint = "https://oai.endpoints.kepler.ai.cloud.ovh.net/v1/",
            SupportsCustomBaseUrl = true,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "scaleway",
            DisplayName = "Scaleway",
            Description = "European cloud AI inference.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields =
            [
                ApiKeyField,
                new("projectId", "Project ID", Required: true, IsSecret: false,
                    HelpText: "Your Scaleway project ID."),
                new("region", "Region", Required: false, IsSecret: false,
                    HelpText: "e.g. fr-par (defaults to fr-par)."),
            ],
            DefaultEndpoint = "https://api.scaleway.ai/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "stackit",
            DisplayName = "STACKIT",
            Description = "German cloud AI inference.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields =
            [
                ApiKeyField,
                new("projectId", "Project ID", Required: true, IsSecret: false,
                    HelpText: "Your STACKIT project ID."),
            ],
            DefaultEndpoint = "https://api.openai.stackit.cloud/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },

        // ── API Key + extra config ─────────────────────────────────────────────
        new()
        {
            Id = "azure-openai",
            DisplayName = "Azure OpenAI",
            Description = "OpenAI models hosted on Microsoft Azure.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields =
            [
                ApiKeyField,
                new("resourceName", "Resource Name", Required: true, IsSecret: false,
                    HelpText: "Your Azure OpenAI resource name (from the Azure portal)."),
            ],
            DefaultEndpoint = null, // Constructed from resource name
            SupportsCustomBaseUrl = true,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "azure-cognitive-services",
            DisplayName = "Azure Cognitive Services",
            Description = "AI models via Azure Cognitive Services.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields =
            [
                ApiKeyField,
                new("resourceName", "Resource Name", Required: true, IsSecret: false,
                    HelpText: "Your Azure Cognitive Services resource name."),
            ],
            DefaultEndpoint = null,
            SupportsCustomBaseUrl = true,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "cloudflare-ai-gateway",
            DisplayName = "Cloudflare AI Gateway",
            Description = "Unified AI gateway with optional billing across providers.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields =
            [
                new("accountId", "Account ID", Required: true, IsSecret: false,
                    HelpText: "Your Cloudflare account ID."),
                new("gatewayId", "Gateway ID", Required: true, IsSecret: false,
                    HelpText: "Your Cloudflare AI Gateway ID."),
                new("apiToken", "API Token", Required: true, IsSecret: true,
                    HelpText: "Your Cloudflare API token."),
            ],
            DefaultEndpoint = null, // Constructed from accountId + gatewayId
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "cloudflare-workers-ai",
            DisplayName = "Cloudflare Workers AI",
            Description = "Run AI models on Cloudflare's global network.",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields =
            [
                new("accountId", "Account ID", Required: true, IsSecret: false,
                    HelpText: "Your Cloudflare account ID."),
                ApiKeyField,
            ],
            DefaultEndpoint = null, // Constructed from accountId
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "sap-ai-core",
            DisplayName = "SAP AI Core",
            Description = "40+ models via SAP AI Core (requires BTP service key).",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields =
            [
                new("serviceKey", "Service Key JSON", Required: true, IsSecret: true,
                    HelpText: "JSON service key from your SAP BTP Cockpit (contains clientid, clientsecret, url, serviceurls)."),
            ],
            DefaultEndpoint = null,
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = false,
            ModelPrefixes = [],
        },

        // ── OAuth Browser Flow ─────────────────────────────────────────────────
        new()
        {
            Id = "digitalocean",
            DisplayName = "DigitalOcean",
            Description = "Open models and Inference Routers via DigitalOcean.",
            AuthMechanism = AuthMechanism.OAuthBrowser,
            CredentialFields =
            [
                new("accessToken", "Model Access Key", Required: true, IsSecret: true,
                    HelpText: "Obtained via OAuth or pasted from the DigitalOcean console."),
            ],
            DefaultEndpoint = "https://inference.do-ai.run/v1/",
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "gitlab",
            DisplayName = "GitLab Duo",
            Description = "GitLab Duo Agent Platform (requires Premium/Ultimate subscription).",
            AuthMechanism = AuthMechanism.OAuthBrowser,
            CredentialFields =
            [
                new("token", "Access Token", Required: true, IsSecret: true,
                    HelpText: "GitLab OAuth token or Personal Access Token (glpat-...) with 'api' scope."),
                new("instanceUrl", "Instance URL", Required: false, IsSecret: false,
                    HelpText: "For self-hosted GitLab. Defaults to https://gitlab.com."),
            ],
            DefaultEndpoint = null,
            SupportsCustomBaseUrl = true,
            IsOpenAiCompatible = false,
            ModelPrefixes = ["duo-chat", "duo-workflow"],
        },

        // ── AWS Credential Chain ───────────────────────────────────────────────
        new()
        {
            Id = "amazon-bedrock",
            DisplayName = "Amazon Bedrock",
            Description = "Claude, Llama, and other models via AWS Bedrock.",
            AuthMechanism = AuthMechanism.AwsCredentialChain,
            CredentialFields =
            [
                new("region", "AWS Region", Required: false, IsSecret: false,
                    HelpText: "e.g. us-east-1 (defaults to us-east-1)."),
                new("profile", "AWS Profile", Required: false, IsSecret: false,
                    HelpText: "Named profile from ~/.aws/credentials. Leave empty to use default credential chain."),
                new("bearerToken", "Bearer Token", Required: false, IsSecret: true,
                    HelpText: "Long-term API key from the Amazon Bedrock console. Takes precedence over profile/keys."),
            ],
            DefaultEndpoint = null,
            SupportsCustomBaseUrl = true,
            IsOpenAiCompatible = false,
            ModelPrefixes = [],
        },

        // ── Service Account / File-based ───────────────────────────────────────
        new()
        {
            Id = "google-vertex",
            DisplayName = "Google Vertex AI",
            Description = "Gemini and partner models via Google Cloud Vertex AI.",
            AuthMechanism = AuthMechanism.ServiceAccountFile,
            CredentialFields =
            [
                new("projectId", "Project ID", Required: true, IsSecret: false,
                    HelpText: "Your Google Cloud project ID."),
                new("serviceAccountJson", "Service Account JSON", Required: false, IsSecret: true,
                    HelpText: "Service account key JSON. Leave empty to use gcloud CLI application default credentials."),
                new("location", "Location", Required: false, IsSecret: false,
                    HelpText: "Vertex AI region (defaults to global)."),
            ],
            DefaultEndpoint = null,
            SupportsCustomBaseUrl = false,
            IsOpenAiCompatible = false,
            ModelPrefixes = ["gemini"],
        },

        // ── No auth (local) ────────────────────────────────────────────────────
        new()
        {
            Id = "ollama",
            DisplayName = "Ollama",
            Description = "Run local models via Ollama.",
            AuthMechanism = AuthMechanism.None,
            CredentialFields = [OptionalApiKeyField],
            DefaultEndpoint = "http://localhost:11434/v1/",
            SupportsCustomBaseUrl = true,
            IsOpenAiCompatible = true,
            CredentialOptional = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "lmstudio",
            DisplayName = "LM Studio",
            Description = "Run local models via LM Studio.",
            AuthMechanism = AuthMechanism.None,
            CredentialFields = [OptionalApiKeyField],
            DefaultEndpoint = "http://127.0.0.1:1234/v1/",
            SupportsCustomBaseUrl = true,
            IsOpenAiCompatible = true,
            CredentialOptional = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "llamacpp",
            DisplayName = "llama.cpp",
            Description = "Run local models via llama-server.",
            AuthMechanism = AuthMechanism.None,
            CredentialFields = [OptionalApiKeyField],
            DefaultEndpoint = "http://127.0.0.1:8080/v1/",
            SupportsCustomBaseUrl = true,
            IsOpenAiCompatible = true,
            CredentialOptional = true,
            ModelPrefixes = [],
        },
        new()
        {
            Id = "atomic-chat",
            DisplayName = "Atomic Chat",
            Description = "Run local models via Atomic Chat desktop app.",
            AuthMechanism = AuthMechanism.None,
            CredentialFields = [OptionalApiKeyField],
            DefaultEndpoint = "http://127.0.0.1:1337/v1/",
            SupportsCustomBaseUrl = true,
            IsOpenAiCompatible = true,
            CredentialOptional = true,
            ModelPrefixes = [],
        },

        // ── Custom (OpenAI-compatible endpoint) ────────────────────────────────
        new()
        {
            Id = "custom",
            DisplayName = "Custom",
            Description = "Any OpenAI-compatible endpoint (proxies, self-hosted models, etc.).",
            AuthMechanism = AuthMechanism.Custom,
            CredentialFields =
            [
                new("baseUrl", "Base URL", Required: true, IsSecret: false,
                    HelpText: "The OpenAI-compatible endpoint URL (e.g. http://localhost:11434/v1/)."),
                OptionalApiKeyField,
            ],
            DefaultEndpoint = null,
            SupportsCustomBaseUrl = true,
            IsOpenAiCompatible = true,
            CredentialOptional = true,
            ModelPrefixes = [],
        },
    ];
}
