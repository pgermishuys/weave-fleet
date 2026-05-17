# OpenCode Provider System Research

**Date**: May 17, 2026  
**Purpose**: Document OpenCode's provider system and identify gaps vs NuCode implementation  
**Status**: Complete

---

## Executive Summary

OpenCode supports **75+ LLM providers** through the [AI SDK](https://ai-sdk.dev/) and [Models.dev](https://models.dev). OpenCode is **provider-agnostic** by design, allowing users to bring their own credentials for any supported provider.

NuCode currently supports only **3 providers** (Anthropic, OpenAI, GitHub Copilot) via hardcoded `ChatClientFactory`.

---

## OpenCode Provider Architecture

### Core Philosophy
- **Provider-agnostic**: Not locked to any single LLM provider
- **Flexible credentials**: Users supply API keys via `/connect` command
- **Configuration-driven**: Provider settings in `opencode.json`
- **Extensible**: Custom providers can be defined via config

### Provider Configuration Structure

**Location**: `~/.local/share/opencode/auth.json` (credentials)  
**Config File**: `opencode.json` with `provider` section

**Example Config**:
```json
{
  "$schema": "https://opencode.ai/config.json",
  "provider": {
    "anthropic": {
      "options": {
        "baseURL": "https://api.anthropic.com/v1"
      }
    }
  }
}
```

**Standard Provider Options**:
- `baseURL` - Custom endpoint (for proxies, self-hosted, VPC endpoints)
- `apiKey` - API key (can be omitted if in credentials store)
- `timeout` - Request timeout
- Provider-specific options (region, profile, headers, etc.)

---

## Complete OpenCode Provider Directory (75+ Providers)

### Tier 1: Premium/Recommended
1. **OpenCode Zen** - Curated models tested by OpenCode team
2. **OpenCode Go** - Low-cost subscription tier

### Tier 2: Major Cloud Providers

| Provider | Auth Method | Config Requirements | Key Features |
|----------|-------------|---------------------|--------------|
| **Anthropic** | API Key / Claude Pro | baseURL customizable | Claude models via API or subscription |
| **OpenAI** | API Key / ChatGPT Plus | Standard API endpoint | GPT-4o, o1, o3 models |
| **GitHub Copilot** | OAuth (github.com) | No API key needed | Short-lived token exchange |
| **Google Vertex AI** | Service account JSON / gcloud | Project ID, region, location | Custom inference profiles support |
| **Amazon Bedrock** | AWS credentials/profiles | Region, profile, endpoint/VPC | Bearer token or AWS credential chain |
| **Azure OpenAI** | API Key | Resource name, deployment name | Content filter configuration |
| **Azure Cognitive Services** | API Key | Resource name, deployment name | Similar to Azure OpenAI |

### Tier 3: Specialized/Regional Providers (30+)

**Code-Optimized Specialists**:
- Cerebras (Qwen 3 Coder 480B)
- DeepSeek (DeepSeek V4 Pro)
- Hugging Face Inference
- OpenRouter (40+ model options)
- Together AI
- Fireworks AI

**Local/Self-Hosted**:
- Ollama (local OpenAI-compatible)
- LM Studio (local OpenAI-compatible)
- llama.cpp (local OpenAI-compatible)
- Atomic Chat (local desktop app)
- NVIDIA NIM (on-prem)

**Regional/Enterprise**:
- 302.AI
- Baseten
- Cortecs
- Cloudflare AI Gateway / Workers AI
- DigitalOcean (Inference + Routers)
- Deep Infra
- FrogBot
- Groq
- Helicone (observability gateway)
- IO.NET
- Moonshot AI (Kimi K2)
- MiniMax
- Nebius Token Factory
- Ollama Cloud
- LLM Gateway
- SAP AI Core
- STACKIT
- OVHcloud AI Endpoints
- Scaleway
- Venice AI
- Vercel AI Gateway
- xAI
- Z.AI
- ZenMux

**Platform-Specific**:
- **GitLab Duo** - GitLab Agent Platform (Premium/Ultimate subscription)
  - 3 Claude models: duo-chat-haiku-4-5, duo-chat-sonnet-4-5, duo-chat-opus-4-5
  - DAP workflow models for Workflow Service routing
  - Self-hosted support with environment variables

---

## OpenCode Provider Configuration Patterns

### 1. Standard API Key Pattern
```json
{
  "provider": {
    "openai": {
      "options": {
        "apiKey": "sk-..."
      }
    }
  }
}
```

### 2. Custom Endpoint (Proxy/Self-Hosted)
```json
{
  "provider": {
    "llama.cpp": {
      "npm": "@ai-sdk/openai-compatible",
      "name": "llama-server (local)",
      "options": {
        "baseURL": "http://127.0.0.1:8080/v1"
      },
      "models": {
        "qwen3-coder:a3b": {
          "name": "Qwen3-Coder: a3b-30b",
          "limit": {
            "context": 128000,
            "output": 65536
          }
        }
      }
    }
  }
}
```

### 3. Environment Variable Pattern
```bash
export ANTHROPIC_API_KEY=xxx
export AWS_PROFILE=my-dev-profile
export AZURE_RESOURCE_NAME=my-resource
```

### 4. Complex Auth (AWS/Azure)
```json
{
  "provider": {
    "amazon-bedrock": {
      "options": {
        "region": "us-east-1",
        "profile": "my-aws-profile",
        "endpoint": "https://bedrock-runtime.us-east-1.vpce-xxxxx.amazonaws.com"
      }
    }
  }
}
```

### 5. Custom Headers (Observability/Rate-Limiting)
```json
{
  "provider": {
    "helicone": {
      "npm": "@ai-sdk/openai-compatible",
      "options": {
        "baseURL": "https://ai-gateway.helicone.ai",
        "headers": {
          "Helicone-Cache-Enabled": "true",
          "Helicone-User-Id": "opencode"
        }
      }
    }
  }
}
```

### 6. Multiple Models per Provider
```json
{
  "provider": {
    "openrouter": {
      "models": {
        "moonshotai/kimi-k2": {
          "options": {
            "provider": {
              "order": ["baseten"],
              "allow_fallbacks": false
            }
          }
        },
        "gpt-4o": {}
      }
    }
  }
}
```

---

## NuCode Current Provider Support

### Implemented Providers (3)

#### 1. **Anthropic** (with caveat)
- **Current**: Uses OpenAI SDK pointed at `https://api.anthropic.com/v1/`
- **Models**: `claude-*` (all Anthropic Claude models)
- **Auth**: API key stored in credentials
- **Credential Type**: `anthropic:api-key`
- **Limitation**: TODO comment states "Replace with native Anthropic MEAI adapter when available"

**Code Location**: `ChatClientFactory.cs:31-41`
```csharp
private static IChatClient CreateAnthropicClient(string modelId, string apiKey)
{
    // Use OpenAI-compatible endpoint via Microsoft.Extensions.AI.OpenAI
    // Anthropic doesn't have a native MEAI package yet — use the OpenAI adapter
    // pointed at Anthropic's API.
    // TODO: Replace with native Anthropic MEAI adapter when available.
    var client = new OpenAI.OpenAIClient(
        new System.ClientModel.ApiKeyCredential(apiKey),
        new OpenAI.OpenAIClientOptions { Endpoint = new Uri("https://api.anthropic.com/v1/") });
    return client.GetChatClient(modelId).AsIChatClient();
}
```

#### 2. **OpenAI** (native)
- **Current**: Native OpenAI SDK support
- **Models**: `gpt-*`, `o1-*`, `o3-*`, etc.
- **Auth**: API key stored in credentials
- **Credential Type**: `openai:api-key`

**Code Location**: `ChatClientFactory.cs:43-47`
```csharp
private static IChatClient CreateOpenAIClient(string modelId, string apiKey)
{
    var client = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey));
    return client.GetChatClient(modelId).AsIChatClient();
}
```

#### 3. **GitHub Copilot** (with token exchange)
- **Current**: OpenAI-compatible endpoint at `https://api.githubcopilot.com/`
- **Models**: Claude/GPT via Copilot (see InferProvider logic below)
- **Auth**: GitHub OAuth token exchanged for short-lived Copilot API token
- **Credential Type**: `github:oauth-access-token`
- **Token Exchange**: `CopilotTokenService.ExchangeAsync()` → `https://api.github.com/copilot_internal/v2/token`

**Code Location**: `ChatClientFactory.cs:49-58`, `CopilotTokenService.cs`
```csharp
private static IChatClient CreateCopilotClient(string modelId, string copilotToken)
{
    // GitHub Copilot exposes an OpenAI-compatible chat completions endpoint.
    var client = new OpenAI.OpenAIClient(
        new System.ClientModel.ApiKeyCredential(copilotToken),
        new OpenAI.OpenAIClientOptions { Endpoint = CopilotEndpoint });
    return client.GetChatClient(modelId).AsIChatClient();
}
```

### Provider Inference Logic (NuCodeHarnessRuntime)

**File**: `NuCodeHarnessRuntime.cs:165-219`

NuCode infers provider from model ID:
```
- If modelId contains `/` prefix (e.g., "copilot/claude-sonnet-4-20250514") → use prefix
- Else if modelId starts with `claude` → default to `copilot`
- Else if modelId starts with `gpt`, `o1`, `o3`, `o4` → default to `copilot`
- Else → default to `copilot`
```

**Default credential requirements**:
```csharp
return provider.ToLowerInvariant() switch
{
    "copilot" => CopilotCredentialRequirement,
    "anthropic" => AnthropicCredentialRequirement,
    "openai" => OpenAICredentialRequirement,
    _ => CopilotCredentialRequirement (fallback)
}
```

### NuCode Provider Configuration

**File**: `ProviderConfig.cs`

Partial config support (not fully utilized):
```csharp
public sealed class ProviderConfig
{
    [JsonPropertyName("options")]
    public ProviderOptions? Options { get; init; }
    
    [JsonPropertyName("whitelist")]
    public List<string>? Whitelist { get; init; }
    
    [JsonPropertyName("blacklist")]
    public List<string>? Blacklist { get; init; }
}

public sealed class ProviderOptions
{
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; init; }
    
    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; init; }
    
    [JsonPropertyName("timeout")]
    public int? Timeout { get; init; }
    
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
```

---

## Gap Analysis: What OpenCode Supports That NuCode Doesn't

### Critical Gaps

| OpenCode Feature | NuCode Status | Impact | Complexity |
|------------------|---------------|--------|------------|
| **Custom baseURL** | ❌ Hardcoded | No proxy/self-hosted support | Low |
| **Environment variables** | ❌ Not used | Can't inject credentials at runtime | Low |
| **AWS Bedrock** | ❌ Not implemented | Can't use AWS models | Medium |
| **Azure (both variants)** | ❌ Not implemented | Can't use Azure models | Medium |
| **Google Vertex AI** | ❌ Not implemented | Can't use Google models | Medium |
| **Local models** (Ollama, LM Studio, llama.cpp) | ❌ Not implemented | Can't use local models | Low |
| **OpenAI-compatible proxies** | ❌ Limited | Can't use observability gateways (Helicone, Vercel) | Low |
| **GitLab Duo** | ❌ Not implemented | Can't use GitLab Agent Platform | Medium |
| **Provider configuration file** | ⚠️ Partial | `ProviderConfig` exists but unused by `ChatClientFactory` | Low |
| **Timeout configuration** | ❌ Not passed | No timeout control | Low |
| **Model whitelisting/blacklisting** | ❌ Not used | No access control on models | Low |

### Moderate Gaps

| Feature | OpenCode | NuCode | Gap Severity |
|---------|----------|--------|--------------|
| Model discovery via `/models` | ✅ Dynamic | ❌ Static/hardcoded | Medium |
| OAuth flows (GitHub, GitLab, others) | ✅ Supported | ⚠️ GitHub only | Medium |
| Service account/profile-based auth | ✅ Supported | ❌ Not supported | Medium |
| Custom headers | ✅ Supported | ❌ Not supported | Low |

### Minor Gaps

| Feature | OpenCode | NuCode | Gap Severity |
|---------|----------|--------|--------------|
| Multiple models per provider config | ✅ Supported | ❌ One model at a time | Low |
| Extension data/arbitrary options | ✅ Supported | ❌ Fixed schema | Low |
| VPC/private endpoint support | ✅ Supported (Bedrock, others) | ❌ Not supported | Low |

---

## Recommendations for NuCode Parity

### Phase 1: Foundation (Low Effort, High Impact)
1. **Use `ProviderConfig` in `ChatClientFactory`**
   - Pass `ProviderOptions` (baseURL, timeout, etc.) to client creation
   - Unlock proxy/custom endpoint support with zero new providers

2. **Support environment variable credentials**
   - Check `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, etc. before requiring DB lookup
   - Simplifies local dev and container deployments

3. **Add baseURL customization**
   - Allow users to point Anthropic/OpenAI/Copilot clients to custom endpoints
   - Enables proxy services (Helicone, Vercel AI Gateway) with existing providers

### Phase 2: Local Model Support (Medium Effort, Immediate ROI)
1. **Add Ollama support**
   - Single provider supporting 100+ local models
   - Same OpenAI-compatible endpoint pattern as existing
   - High demand in dev community

2. **Add LM Studio support**
   - Similar to Ollama (OpenAI-compatible)
   - Local GPU acceleration

3. **Add llama.cpp support**
   - Most direct local model runner
   - Lightweight alternative to Ollama

### Phase 3: Cloud Provider Expansion (Medium Effort, Strategic)
1. **AWS Bedrock**
   - `Microsoft.Extensions.AI.Amazon.Bedrock` package available
   - Region/profile configuration support
   - VPC endpoint support

2. **Azure OpenAI**
   - Native `Microsoft.Extensions.AI.AzureOpenAI` package
   - Resource name + deployment ID configuration

3. **Google Vertex AI**
   - `Microsoft.Extensions.AI.GoogleVertex` package
   - Project ID + region configuration

### Phase 4: Advanced (Lower Priority)
1. **GitLab Duo** - Requires GitLab integration infrastructure
2. **Other regional providers** - Based on customer demand
3. **Dynamic model discovery** - Significant UX work
4. **OAuth provider flows** - Auth infrastructure refactor

---

## Configuration Schema for NuCode Parity

### Proposed `opencode.json` Support

```json
{
  "provider": {
    "anthropic": {
      "options": {
        "baseURL": "https://api.anthropic.com/v1",
        "timeout": 30000
      }
    },
    "openai": {
      "options": {
        "baseURL": "https://api.openai.com/v1"
      }
    },
    "ollama": {
      "options": {
        "baseURL": "http://localhost:11434/v1"
      }
    },
    "bedrock": {
      "options": {
        "region": "us-east-1",
        "profile": "my-aws-profile"
      }
    }
  }
}
```

### Proposed Credential Structure

**`WeaveFleet.Domain.Harnesses`**:
```csharp
public class ProviderCredential
{
    public string Provider { get; set; }        // "anthropic", "openai", "bedrock", "ollama"
    public string ModelId { get; set; }         // Optional: restrict credential to specific model
    public string Kind { get; set; }            // "api-key", "aws-profile", "service-account", etc.
    public string EncryptedValue { get; set; }  // API key or config JSON
    public DateTime ExpiresAt { get; set; }     // For short-lived tokens (Copilot)
}
```

---

## OpenCode Provider Registration Mechanism

OpenCode uses the **AI SDK** (`@ai-sdk/*` packages) which provides:
- Standardized interface for all providers
- Built-in model discovery
- Tool calling support across providers
- Structured output support

**Key Packages**:
- `@ai-sdk/openai-compatible` - For OpenAI API compatible services (Ollama, llama.cpp, etc.)
- `@ai-sdk/anthropic` - Native Anthropic SDK
- `@ai-sdk/openai` - Native OpenAI SDK
- `@ai-sdk/amazon-bedrock` - AWS Bedrock
- `@ai-sdk/google-vertex` - Google Vertex AI
- And 30+ more for various providers

**NuCode Equivalent**: Need Microsoft.Extensions.AI packages or direct client SDK usage.

---

## Files to Review for Implementation

### OpenCode Source
- Main: `packages/cli/src/providers/` (provider implementations)
- Config: `packages/cli/src/config.ts` (schema loading)
- Auth: `packages/cli/src/auth/` (credential storage)

### NuCode Source
- Current: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/ChatClientFactory.cs`
- Config: `src/NuCode/Configuration/ProviderConfig.cs`
- Token exchange: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/CopilotTokenService.cs`
- Runtime: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarnessRuntime.cs`

---

## Conclusion

OpenCode's strength is **provider agnosticity** — it's designed from day one to support any LLM provider without coupling. NuCode's current implementation is **provider-specific** (Anthropic, OpenAI, GitHub Copilot only).

To achieve parity:
1. **Short-term** (Phase 1): Unlock existing configuration infrastructure + baseURL support
2. **Medium-term** (Phase 2-3): Add local models + cloud providers via MEAI packages
3. **Long-term** (Phase 4): Match OpenCode's full provider ecosystem

The hardcoded nature of `ChatClientFactory` is the primary blocker. Refactoring it to be configuration-driven (using `ProviderConfig`) would unblock most gaps with minimal effort.
