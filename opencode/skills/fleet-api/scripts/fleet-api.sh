#!/usr/bin/env bash
set -euo pipefail

# Fleet API CLI
# Provides programmatic access to the Fleet API for AI agents and automations

# Default configuration
FLEET_API_BASE_URL="${FLEET_API_BASE_URL:-http://localhost:5001}"
FLEET_API_TOKEN="${FLEET_API_TOKEN:-}"

# Colors for output (if terminal supports it)
if [[ -t 1 ]]; then
    RED='\033[0;31m'
    GREEN='\033[0;32m'
    YELLOW='\033[1;33m'
    NC='\033[0m' # No Color
else
    RED=''
    GREEN=''
    YELLOW=''
    NC=''
fi

# Usage/help function
usage() {
    cat << 'EOF'
Fleet API CLI - Interact with the Fleet API programmatically

USAGE:
    fleet-api.sh <action> [options]

GLOBAL OPTIONS:
    -b, --base-url <url>    Override API base URL (default: http://localhost:5001)
    -t, --token <token>     Bearer token for authentication (or set FLEET_API_TOKEN)
    -h, --help              Show this help

ACTIONS:

  Health & Discovery:
    health                  Check Fleet API health
    source-catalog          List available session source providers
    get-event-catalog       List available automation event types

  Sessions:
    list-sessions           List sessions
        [--workspace-id <id>] [--status <status>] [--tags <tags>]
    
    get-session             Get session details
        --id <id>
    
    get-session-origin      Get session origin/provenance
        --id <id>
    
    get-session-messages    Get session messages
        --id <id> [--limit <n>] [--before <cursor>]
    
    get-session-diffs       Get git diffs for session
        --id <id>
    
    get-session-status      Get session status
        --id <id>
    
    create-session          Create a new session
        --directory <path> --title <title>
        [--isolation-strategy <strategy>] [--branch <branch>]
        [--harness-type <type>] [--initial-prompt <prompt>]
        [--tags <tags>] [--project-id <id>]
    
    delete-session          Delete session
        --id <id>
    
    update-session-tags     Update session tags
        --id <id> --tags <tags>
    
    prompt-session          Send prompt to session
        --id <id> --prompt <text>
    
    stop-session            Stop session
        --id <id>
    
    abort-session           Abort session
        --id <id>
    
    resume-session          Resume session
        --id <id>
    
    fork-session            Fork session
        --id <id>
    
    get-session-delegations Get session delegations
        --id <id> [--limit <n>] [--before <cursor>]
    
    browse-session-files    Browse session files
        --id <id> [--path <path>]
    
    get-session-file-content Get session file content
        --id <id> --path <path>
    
    search-session-files    Search session files
        --id <id> --query <query>
    
    get-session-models      Get available models for session
        --id <id>
    
    get-session-agents      Get available agents for session
        --id <id>
    
    add-session-source      Add source to existing session
        --id <id> --provider-id <id> --source-type <type>
        --action-id <id> --input-json <json>
    
    preview-session-source  Preview source before adding
        --provider-id <id> --source-type <type>
        --action-id <id> --input-json <json>

  GitHub Session Convenience:
    create-session-from-github-pr    Create session from GitHub PR
        --owner <owner> --repo <repo> --number <n> --repository-path <path>
        [--title <title>] [--initial-prompt <prompt>]
        [--isolation-strategy <strategy>] [--branch <branch>]
        [--tags <tags>] [--project-id <id>] [--harness-type <type>]
    
    create-session-from-github-issue Create session from GitHub issue
        --owner <owner> --repo <repo> --number <n> --repository-path <path>
        [--title <title>] [--initial-prompt <prompt>]
        [--isolation-strategy <strategy>] [--branch <branch>]
        [--tags <tags>] [--project-id <id>] [--harness-type <type>]

  Automations:
    list-automations        List automations
        [--workspace-id <id>]
    
    get-automation          Get automation
        --id <id>
    
    create-automation       Create automation
        --name <name> --prompt <prompt> --trigger-type <type>
        --trigger-config <json>
        [--max-concurrent-runs <n>] [--max-runs-per-hour <n>]
        [--timeout-minutes <n>] [--workspace-id <id>]
        [--model <model>] [--agent <agent>]
        [--target-tags <tags>] [--target-type <type>]
    
    update-automation       Update automation
        --id <id> [--name <name>] [--prompt <prompt>]
        [--trigger-type <type>] [--trigger-config <json>]
        [--max-concurrent-runs <n>] [--max-runs-per-hour <n>]
        [--timeout-minutes <n>] [--target-tags <tags>]
        [--target-type <type>]
    
    delete-automation       Delete automation
        --id <id>
    
    enable-automation       Enable automation
        --id <id>
    
    disable-automation      Disable automation
        --id <id>
    
    run-automation          Manually trigger automation
        --id <id>

  GitHub Integration:
    list-github-repos       List GitHub repositories
    
    list-github-pulls       List pull requests
        --owner <owner> --repo <repo> [--state <state>]
    
    get-github-pr           Get pull request detail
        --owner <owner> --repo <repo> --number <n>
    
    list-github-pr-comments List PR comments
        --owner <owner> --repo <repo> --number <n>
    
    list-github-issues      List issues
        --owner <owner> --repo <repo> [--state <state>]
    
    get-github-issue        Get issue detail
        --owner <owner> --repo <repo> --number <n>
    
    list-github-issue-comments List issue comments
        --owner <owner> --repo <repo> --number <n>
    
    search-github-issues    Search issues
        --owner <owner> --repo <repo> [--query <query>]

EXAMPLES:

  # Check health
  fleet-api.sh health

  # List open PRs
  fleet-api.sh list-github-pulls --owner myorg --repo myrepo --state open

  # Create session from PR
  fleet-api.sh create-session-from-github-pr \
    --owner myorg --repo myrepo --number 42 \
    --repository-path /path/to/repo \
    --tags "github-pr,review-requested" \
    --initial-prompt "Review this pull request"

  # Create automation
  fleet-api.sh create-automation \
    --name "Auto-review PRs" \
    --prompt "Review this pull request" \
    --trigger-type event \
    --trigger-config '{"eventType":"session.created"}' \
    --target-type tagged_session \
    --target-tags "github-pr,review-requested"

EOF
}

# Error handling
error() {
    echo -e "${RED}Error: $1${NC}" >&2
    echo "{\"error\":\"$1\"}" | jq -c '.'
    exit 1
}

# HTTP request wrapper
http_request() {
    local method="$1"
    local path="$2"
    local data="${3:-}"
    local url="${FLEET_API_BASE_URL}${path}"
    
    local response
    local http_code
    local temp_file
    temp_file=$(mktemp)
    
    local auth_args=()
    if [[ -n "$FLEET_API_TOKEN" ]]; then
        auth_args+=(-H "Authorization: Bearer $FLEET_API_TOKEN")
    fi
    
    if [[ -n "$data" ]]; then
        http_code=$(curl -s -w "%{http_code}" -X "$method" \
            -H "Content-Type: application/json" \
            "${auth_args[@]+"${auth_args[@]}"}" \
            -d "$data" \
            -o "$temp_file" \
            "$url")
    else
        http_code=$(curl -s -w "%{http_code}" -X "$method" \
            "${auth_args[@]+"${auth_args[@]}"}" \
            -o "$temp_file" \
            "$url")
    fi
    
    response=$(cat "$temp_file")
    rm -f "$temp_file"
    
    if [[ "$http_code" -ge 200 && "$http_code" -lt 300 ]]; then
        echo "$response"
    else
        # Try to parse error from response
        local error_msg
        error_msg=$(echo "$response" | jq -r '.error // .message // .title // "HTTP error"' 2>/dev/null || echo "HTTP error")
        echo "{\"error\":\"$error_msg\",\"statusCode\":$http_code}" | jq -c '.'
        exit 1
    fi
}

# Parse command line arguments
parse_args() {
    while [[ $# -gt 0 ]]; do
        case $1 in
            -b|--base-url)
                FLEET_API_BASE_URL="$2"
                shift 2
                ;;
            -t|--token)
                FLEET_API_TOKEN="$2"
                shift 2
                ;;
            -h|--help)
                usage
                exit 0
                ;;
            --id)
                ID="$2"
                shift 2
                ;;
            --owner)
                OWNER="$2"
                shift 2
                ;;
            --repo)
                REPO="$2"
                shift 2
                ;;
            --number)
                NUMBER="$2"
                shift 2
                ;;
            --tags)
                TAGS="$2"
                shift 2
                ;;
            --prompt)
                PROMPT="$2"
                shift 2
                ;;
            --title)
                TITLE="$2"
                shift 2
                ;;
            --directory)
                DIRECTORY="$2"
                shift 2
                ;;
            --repository-path)
                REPOSITORY_PATH="$2"
                shift 2
                ;;
            --isolation-strategy)
                ISOLATION_STRATEGY="$2"
                shift 2
                ;;
            --branch)
                BRANCH="$2"
                shift 2
                ;;
            --harness-type)
                HARNESS_TYPE="$2"
                shift 2
                ;;
            --initial-prompt)
                INITIAL_PROMPT="$2"
                shift 2
                ;;
            --project-id)
                PROJECT_ID="$2"
                shift 2
                ;;
            --workspace-id)
                WORKSPACE_ID="$2"
                shift 2
                ;;
            --status)
                STATUS="$2"
                shift 2
                ;;
            --limit)
                LIMIT="$2"
                shift 2
                ;;
            --before)
                BEFORE="$2"
                shift 2
                ;;
            --state)
                STATE="$2"
                shift 2
                ;;
            --provider-id)
                PROVIDER_ID="$2"
                shift 2
                ;;
            --source-type)
                SOURCE_TYPE="$2"
                shift 2
                ;;
            --action-id)
                ACTION_ID="$2"
                shift 2
                ;;
            --input-json)
                INPUT_JSON="$2"
                shift 2
                ;;
            --name)
                NAME="$2"
                shift 2
                ;;
            --trigger-type)
                TRIGGER_TYPE="$2"
                shift 2
                ;;
            --trigger-config)
                TRIGGER_CONFIG="$2"
                shift 2
                ;;
            --max-concurrent-runs)
                MAX_CONCURRENT_RUNS="$2"
                shift 2
                ;;
            --max-runs-per-hour)
                MAX_RUNS_PER_HOUR="$2"
                shift 2
                ;;
            --timeout-minutes)
                TIMEOUT_MINUTES="$2"
                shift 2
                ;;
            --model)
                MODEL="$2"
                shift 2
                ;;
            --agent)
                AGENT="$2"
                shift 2
                ;;
            --target-tags)
                TARGET_TAGS="$2"
                shift 2
                ;;
            --target-type)
                TARGET_TYPE="$2"
                shift 2
                ;;
            --path)
                FILE_PATH="$2"
                shift 2
                ;;
            --query)
                QUERY="$2"
                shift 2
                ;;
            *)
                error "Unknown option: $1"
                ;;
        esac
    done
}

# Action: health
action_health() {
    http_request GET "/api/fleet/summary"
}

# Action: source-catalog
action_source_catalog() {
    http_request GET "/api/session-sources/catalog"
}

# Action: get-event-catalog
action_get_event_catalog() {
    http_request GET "/api/automations/event-catalog"
}

# Action: list-sessions
action_list_sessions() {
    local query=""
    [[ -n "${WORKSPACE_ID:-}" ]] && query="${query}&workspaceId=${WORKSPACE_ID}"
    [[ -n "${STATUS:-}" ]] && query="${query}&status=${STATUS}"
    [[ -n "${TAGS:-}" ]] && query="${query}&tags=${TAGS}"
    query="${query#&}" # Remove leading &
    [[ -n "$query" ]] && query="?${query}"
    
    http_request GET "/api/sessions${query}"
}

# Action: get-session
action_get_session() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    http_request GET "/api/sessions/${ID}"
}

# Action: get-session-origin
action_get_session_origin() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    http_request GET "/api/sessions/${ID}/origin"
}

# Action: get-session-messages
action_get_session_messages() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    local query=""
    [[ -n "${LIMIT:-}" ]] && query="${query}&limit=${LIMIT}"
    [[ -n "${BEFORE:-}" ]] && query="${query}&before=${BEFORE}"
    query="${query#&}"
    [[ -n "$query" ]] && query="?${query}"
    
    http_request GET "/api/sessions/${ID}/messages${query}"
}

# Action: get-session-diffs
action_get_session_diffs() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    http_request GET "/api/sessions/${ID}/diffs"
}

# Action: get-session-status
action_get_session_status() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    http_request GET "/api/sessions/${ID}/status"
}

# Action: delete-session
action_delete_session() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    http_request DELETE "/api/sessions/${ID}"
}

# Action: update-session-tags
action_update_session_tags() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    [[ -z "${TAGS:-}" ]] && error "Missing required parameter: --tags"
    
    local tags_array
    tags_array=$(echo "$TAGS" | jq -R 'split(",")')
    local body
    body=$(jq -n --argjson tags "$tags_array" '{tags: $tags}')
    
    http_request PATCH "/api/sessions/${ID}/tags" "$body"
}

# Action: get-session-delegations
action_get_session_delegations() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    local query=""
    [[ -n "${LIMIT:-}" ]] && query="${query}&limit=${LIMIT}"
    [[ -n "${BEFORE:-}" ]] && query="${query}&before=${BEFORE}"
    query="${query#&}"
    [[ -n "$query" ]] && query="?${query}"
    
    http_request GET "/api/sessions/${ID}/delegations${query}"
}

# Action: browse-session-files
action_browse_session_files() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    local query=""
    [[ -n "${FILE_PATH:-}" ]] && query="?path=${FILE_PATH}"
    
    http_request GET "/api/sessions/${ID}/files/browse${query}"
}

# Action: get-session-file-content
action_get_session_file_content() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    [[ -z "${FILE_PATH:-}" ]] && error "Missing required parameter: --path"
    
    http_request GET "/api/sessions/${ID}/files/content?path=${FILE_PATH}"
}

# Action: search-session-files
action_search_session_files() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    [[ -z "${QUERY:-}" ]] && error "Missing required parameter: --query"
    
    http_request GET "/api/sessions/${ID}/find/files?q=${QUERY}"
}

# Action: get-session-models
action_get_session_models() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    http_request GET "/api/sessions/${ID}/models"
}

# Action: get-session-agents
action_get_session_agents() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    http_request GET "/api/sessions/${ID}/agents"
}

# Action: create-session
action_create_session() {
    [[ -z "${DIRECTORY:-}" ]] && error "Missing required parameter: --directory"
    [[ -z "${TITLE:-}" ]] && error "Missing required parameter: --title"
    
    local body
    body=$(jq -n \
        --arg directory "$DIRECTORY" \
        --arg title "$TITLE" \
        --arg isolationStrategy "${ISOLATION_STRATEGY:-existing}" \
        --arg branch "${BRANCH:-}" \
        --arg harnessType "${HARNESS_TYPE:-opencode}" \
        --arg initialPrompt "${INITIAL_PROMPT:-}" \
        --arg projectId "${PROJECT_ID:-}" \
        '{
            directory: $directory,
            title: $title,
            isolationStrategy: $isolationStrategy,
            harnessType: $harnessType
        }
        | if $branch != "" then .branch = $branch else . end
        | if $initialPrompt != "" then .initialPrompt = $initialPrompt else . end
        | if $projectId != "" then .projectId = $projectId else . end
        ')
    
    # Add tags if provided
    if [[ -n "${TAGS:-}" ]]; then
        local tags_array
        tags_array=$(echo "$TAGS" | jq -R 'split(",")')
        body=$(echo "$body" | jq --argjson tags "$tags_array" '. + {tags: $tags}')
    fi
    
    http_request POST "/api/sessions" "$body"
}

# Action: prompt-session
action_prompt_session() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    [[ -z "${PROMPT:-}" ]] && error "Missing required parameter: --prompt"
    
    local body
    body=$(jq -n --arg prompt "$PROMPT" '{prompt: $prompt}')
    
    http_request POST "/api/sessions/${ID}/prompt" "$body"
}

# Action: stop-session
action_stop_session() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    http_request POST "/api/sessions/${ID}/stop" "{}"
}

# Action: abort-session
action_abort_session() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    http_request POST "/api/sessions/${ID}/abort" "{}"
}

# Action: resume-session
action_resume_session() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    http_request POST "/api/sessions/${ID}/resume" "{}"
}

# Action: fork-session
action_fork_session() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    http_request POST "/api/sessions/${ID}/fork" "{}"
}

# Action: add-session-source
action_add_session_source() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    [[ -z "${PROVIDER_ID:-}" ]] && error "Missing required parameter: --provider-id"
    [[ -z "${SOURCE_TYPE:-}" ]] && error "Missing required parameter: --source-type"
    [[ -z "${ACTION_ID:-}" ]] && error "Missing required parameter: --action-id"
    [[ -z "${INPUT_JSON:-}" ]] && error "Missing required parameter: --input-json"
    
    # Validate input JSON
    if ! echo "$INPUT_JSON" | jq empty 2>/dev/null; then
        error "Invalid JSON in --input-json"
    fi
    
    local body
    body=$(jq -n \
        --arg providerId "$PROVIDER_ID" \
        --arg sourceType "$SOURCE_TYPE" \
        --arg actionId "$ACTION_ID" \
        --argjson input "$INPUT_JSON" \
        '{
            key: {
                providerId: $providerId,
                sourceType: $sourceType,
                actionId: $actionId
            },
            input: $input
        }')
    
    http_request POST "/api/sessions/${ID}/sources" "$body"
}

# Action: preview-session-source
action_preview_session_source() {
    [[ -z "${PROVIDER_ID:-}" ]] && error "Missing required parameter: --provider-id"
    [[ -z "${SOURCE_TYPE:-}" ]] && error "Missing required parameter: --source-type"
    [[ -z "${ACTION_ID:-}" ]] && error "Missing required parameter: --action-id"
    [[ -z "${INPUT_JSON:-}" ]] && error "Missing required parameter: --input-json"
    
    # Validate input JSON
    if ! echo "$INPUT_JSON" | jq empty 2>/dev/null; then
        error "Invalid JSON in --input-json"
    fi
    
    local body
    body=$(jq -n \
        --arg providerId "$PROVIDER_ID" \
        --arg sourceType "$SOURCE_TYPE" \
        --arg actionId "$ACTION_ID" \
        --argjson input "$INPUT_JSON" \
        '{
            key: {
                providerId: $providerId,
                sourceType: $sourceType,
                actionId: $actionId
            },
            input: $input
        }')
    
    http_request POST "/api/sessions/source-preview" "$body"
}

# Action: create-session-from-github-pr
action_create_session_from_github_pr() {
    [[ -z "${OWNER:-}" ]] && error "Missing required parameter: --owner"
    [[ -z "${REPO:-}" ]] && error "Missing required parameter: --repo"
    [[ -z "${NUMBER:-}" ]] && error "Missing required parameter: --number"
    [[ -z "${REPOSITORY_PATH:-}" ]] && error "Missing required parameter: --repository-path"
    
    # Build source input
    local source_input
    source_input=$(jq -n \
        --arg owner "$OWNER" \
        --arg repo "$REPO" \
        --argjson number "$NUMBER" \
        --arg repositoryPath "$REPOSITORY_PATH" \
        --arg isolationStrategy "${ISOLATION_STRATEGY:-worktree}" \
        --arg branch "${BRANCH:-}" \
        '{
            owner: $owner,
            repo: $repo,
            number: $number,
            repositoryPath: $repositoryPath,
            isolationStrategy: $isolationStrategy
        }
        | if $branch != "" then .branch = $branch else . end
        ')
    
    # Build session body
    local body
    body=$(jq -n \
        --arg directory "$REPOSITORY_PATH" \
        --arg title "${TITLE:-PR #${NUMBER}: ${OWNER}/${REPO}}" \
        --arg isolationStrategy "${ISOLATION_STRATEGY:-worktree}" \
        --arg harnessType "${HARNESS_TYPE:-opencode}" \
        --arg initialPrompt "${INITIAL_PROMPT:-}" \
        --arg projectId "${PROJECT_ID:-}" \
        --argjson sourceInput "$source_input" \
        '{
            directory: $directory,
            title: $title,
            isolationStrategy: $isolationStrategy,
            harnessType: $harnessType,
            source: {
                key: {
                    providerId: "builtin.github",
                    sourceType: "github-pull-request",
                    actionId: "start-session",
                    contractVersion: 1
                },
                input: $sourceInput
            }
        }
        | if $initialPrompt != "" then .initialPrompt = $initialPrompt else . end
        | if $projectId != "" then .projectId = $projectId else . end
        ')
    
    # Add tags if provided
    if [[ -n "${TAGS:-}" ]]; then
        local tags_array
        tags_array=$(echo "$TAGS" | jq -R 'split(",")')
        body=$(echo "$body" | jq --argjson tags "$tags_array" '. + {tags: $tags}')
    fi
    
    http_request POST "/api/sessions" "$body"
}

# Action: create-session-from-github-issue
action_create_session_from_github_issue() {
    [[ -z "${OWNER:-}" ]] && error "Missing required parameter: --owner"
    [[ -z "${REPO:-}" ]] && error "Missing required parameter: --repo"
    [[ -z "${NUMBER:-}" ]] && error "Missing required parameter: --number"
    [[ -z "${REPOSITORY_PATH:-}" ]] && error "Missing required parameter: --repository-path"
    
    # Build source input
    local source_input
    source_input=$(jq -n \
        --arg owner "$OWNER" \
        --arg repo "$REPO" \
        --argjson number "$NUMBER" \
        --arg repositoryPath "$REPOSITORY_PATH" \
        --arg isolationStrategy "${ISOLATION_STRATEGY:-existing}" \
        --arg branch "${BRANCH:-}" \
        '{
            owner: $owner,
            repo: $repo,
            number: $number,
            repositoryPath: $repositoryPath,
            isolationStrategy: $isolationStrategy
        }
        | if $branch != "" then .branch = $branch else . end
        ')
    
    # Build session body
    local body
    body=$(jq -n \
        --arg directory "$REPOSITORY_PATH" \
        --arg title "${TITLE:-Issue #${NUMBER}: ${OWNER}/${REPO}}" \
        --arg isolationStrategy "${ISOLATION_STRATEGY:-existing}" \
        --arg harnessType "${HARNESS_TYPE:-opencode}" \
        --arg initialPrompt "${INITIAL_PROMPT:-}" \
        --arg projectId "${PROJECT_ID:-}" \
        --argjson sourceInput "$source_input" \
        '{
            directory: $directory,
            title: $title,
            isolationStrategy: $isolationStrategy,
            harnessType: $harnessType,
            source: {
                key: {
                    providerId: "builtin.github",
                    sourceType: "github-issue",
                    actionId: "start-session",
                    contractVersion: 1
                },
                input: $sourceInput
            }
        }
        | if $initialPrompt != "" then .initialPrompt = $initialPrompt else . end
        | if $projectId != "" then .projectId = $projectId else . end
        ')
    
    # Add tags if provided
    if [[ -n "${TAGS:-}" ]]; then
        local tags_array
        tags_array=$(echo "$TAGS" | jq -R 'split(",")')
        body=$(echo "$body" | jq --argjson tags "$tags_array" '. + {tags: $tags}')
    fi
    
    http_request POST "/api/sessions" "$body"
}

# Action: list-automations
action_list_automations() {
    local query=""
    [[ -n "${WORKSPACE_ID:-}" ]] && query="?workspaceId=${WORKSPACE_ID}"
    
    http_request GET "/api/automations${query}"
}

# Action: get-automation
action_get_automation() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    http_request GET "/api/automations/${ID}"
}

# Action: create-automation
action_create_automation() {
    [[ -z "${NAME:-}" ]] && error "Missing required parameter: --name"
    [[ -z "${PROMPT:-}" ]] && error "Missing required parameter: --prompt"
    [[ -z "${TRIGGER_TYPE:-}" ]] && error "Missing required parameter: --trigger-type"
    [[ -z "${TRIGGER_CONFIG:-}" ]] && error "Missing required parameter: --trigger-config"
    
    # Validate trigger config JSON
    if ! echo "$TRIGGER_CONFIG" | jq empty 2>/dev/null; then
        error "Invalid JSON in --trigger-config"
    fi
    
    local body
    body=$(jq -n \
        --arg name "$NAME" \
        --arg prompt "$PROMPT" \
        --arg triggerType "$TRIGGER_TYPE" \
        --arg triggerConfig "$TRIGGER_CONFIG" \
        --argjson maxConcurrentRuns "${MAX_CONCURRENT_RUNS:-2}" \
        --argjson maxRunsPerHour "${MAX_RUNS_PER_HOUR:-20}" \
        --argjson timeoutMinutes "${TIMEOUT_MINUTES:-30}" \
        --arg workspaceId "${WORKSPACE_ID:-}" \
        --arg model "${MODEL:-}" \
        --arg agent "${AGENT:-}" \
        --arg targetTags "${TARGET_TAGS:-}" \
        --arg targetType "${TARGET_TYPE:-tagged_session}" \
        '{
            name: $name,
            prompt: $prompt,
            triggerType: $triggerType,
            triggerConfig: $triggerConfig,
            maxConcurrentRuns: $maxConcurrentRuns,
            maxRunsPerHour: $maxRunsPerHour,
            timeoutMinutes: $timeoutMinutes,
            targetType: $targetType
        }
        | if $workspaceId != "" then .workspaceId = $workspaceId else . end
        | if $model != "" then .model = $model else . end
        | if $agent != "" then .agent = $agent else . end
        ')
    
    # Add target tags if provided
    if [[ -n "${TARGET_TAGS:-}" ]]; then
        local tags_array
        tags_array=$(echo "$TARGET_TAGS" | jq -R 'split(",")')
        body=$(echo "$body" | jq --argjson tags "$tags_array" '. + {targetTags: $tags}')
    fi
    
    http_request POST "/api/automations" "$body"
}

# Action: update-automation
action_update_automation() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    
    # Build update body with only provided fields
    local body="{}"
    
    [[ -n "${NAME:-}" ]] && body=$(echo "$body" | jq --arg name "$NAME" '. + {name: $name}')
    [[ -n "${PROMPT:-}" ]] && body=$(echo "$body" | jq --arg prompt "$PROMPT" '. + {prompt: $prompt}')
    [[ -n "${TRIGGER_TYPE:-}" ]] && body=$(echo "$body" | jq --arg triggerType "$TRIGGER_TYPE" '. + {triggerType: $triggerType}')
    [[ -n "${TRIGGER_CONFIG:-}" ]] && body=$(echo "$body" | jq --arg triggerConfig "$TRIGGER_CONFIG" '. + {triggerConfig: $triggerConfig}')
    [[ -n "${MAX_CONCURRENT_RUNS:-}" ]] && body=$(echo "$body" | jq --argjson maxConcurrentRuns "$MAX_CONCURRENT_RUNS" '. + {maxConcurrentRuns: $maxConcurrentRuns}')
    [[ -n "${MAX_RUNS_PER_HOUR:-}" ]] && body=$(echo "$body" | jq --argjson maxRunsPerHour "$MAX_RUNS_PER_HOUR" '. + {maxRunsPerHour: $maxRunsPerHour}')
    [[ -n "${TIMEOUT_MINUTES:-}" ]] && body=$(echo "$body" | jq --argjson timeoutMinutes "$TIMEOUT_MINUTES" '. + {timeoutMinutes: $timeoutMinutes}')
    [[ -n "${TARGET_TYPE:-}" ]] && body=$(echo "$body" | jq --arg targetType "$TARGET_TYPE" '. + {targetType: $targetType}')
    
    if [[ -n "${TARGET_TAGS:-}" ]]; then
        local tags_array
        tags_array=$(echo "$TARGET_TAGS" | jq -R 'split(",")')
        body=$(echo "$body" | jq --argjson tags "$tags_array" '. + {targetTags: $tags}')
    fi
    
    http_request PUT "/api/automations/${ID}" "$body"
}

# Action: delete-automation
action_delete_automation() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    http_request DELETE "/api/automations/${ID}"
}

# Action: enable-automation
action_enable_automation() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    http_request POST "/api/automations/${ID}/enable" "{}"
}

# Action: disable-automation
action_disable_automation() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    http_request POST "/api/automations/${ID}/disable" "{}"
}

# Action: run-automation
action_run_automation() {
    [[ -z "${ID:-}" ]] && error "Missing required parameter: --id"
    http_request POST "/api/automations/${ID}/run" "{}"
}

# Action: list-github-repos
action_list_github_repos() {
    http_request GET "/api/integrations/github/repos"
}

# Action: list-github-pulls
action_list_github_pulls() {
    [[ -z "${OWNER:-}" ]] && error "Missing required parameter: --owner"
    [[ -z "${REPO:-}" ]] && error "Missing required parameter: --repo"
    
    local query=""
    [[ -n "${STATE:-}" ]] && query="?state=${STATE}"
    
    http_request GET "/api/integrations/github/repos/${OWNER}/${REPO}/pulls${query}"
}

# Action: list-github-issues
action_list_github_issues() {
    [[ -z "${OWNER:-}" ]] && error "Missing required parameter: --owner"
    [[ -z "${REPO:-}" ]] && error "Missing required parameter: --repo"
    
    local query=""
    [[ -n "${STATE:-}" ]] && query="?state=${STATE}"
    
    http_request GET "/api/integrations/github/repos/${OWNER}/${REPO}/issues${query}"
}

# Action: get-github-issue
action_get_github_issue() {
    [[ -z "${OWNER:-}" ]] && error "Missing required parameter: --owner"
    [[ -z "${REPO:-}" ]] && error "Missing required parameter: --repo"
    [[ -z "${NUMBER:-}" ]] && error "Missing required parameter: --number"
    
    http_request GET "/api/integrations/github/repos/${OWNER}/${REPO}/issues/${NUMBER}"
}

# Action: get-github-pr
action_get_github_pr() {
    [[ -z "${OWNER:-}" ]] && error "Missing required parameter: --owner"
    [[ -z "${REPO:-}" ]] && error "Missing required parameter: --repo"
    [[ -z "${NUMBER:-}" ]] && error "Missing required parameter: --number"
    
    http_request GET "/api/integrations/github/repos/${OWNER}/${REPO}/pulls/${NUMBER}"
}

# Action: list-github-pr-comments
action_list_github_pr_comments() {
    [[ -z "${OWNER:-}" ]] && error "Missing required parameter: --owner"
    [[ -z "${REPO:-}" ]] && error "Missing required parameter: --repo"
    [[ -z "${NUMBER:-}" ]] && error "Missing required parameter: --number"
    
    http_request GET "/api/integrations/github/repos/${OWNER}/${REPO}/pulls/${NUMBER}/comments"
}

# Action: list-github-issue-comments
action_list_github_issue_comments() {
    [[ -z "${OWNER:-}" ]] && error "Missing required parameter: --owner"
    [[ -z "${REPO:-}" ]] && error "Missing required parameter: --repo"
    [[ -z "${NUMBER:-}" ]] && error "Missing required parameter: --number"
    
    http_request GET "/api/integrations/github/repos/${OWNER}/${REPO}/issues/${NUMBER}/comments"
}

# Action: search-github-issues
action_search_github_issues() {
    [[ -z "${OWNER:-}" ]] && error "Missing required parameter: --owner"
    [[ -z "${REPO:-}" ]] && error "Missing required parameter: --repo"
    
    local query=""
    [[ -n "${QUERY:-}" ]] && query="?query=${QUERY}"
    
    http_request GET "/api/integrations/github/repos/${OWNER}/${REPO}/issues/search${query}"
}

# Main entry point
main() {
    if [[ $# -eq 0 ]]; then
        usage
        exit 1
    fi
    
    local action="$1"
    shift
    
    # Parse remaining arguments
    parse_args "$@"
    
    # Dispatch to action
    case "$action" in
        health)
            action_health
            ;;
        source-catalog)
            action_source_catalog
            ;;
        get-event-catalog)
            action_get_event_catalog
            ;;
        list-sessions)
            action_list_sessions
            ;;
        get-session)
            action_get_session
            ;;
        get-session-origin)
            action_get_session_origin
            ;;
        get-session-messages)
            action_get_session_messages
            ;;
        get-session-diffs)
            action_get_session_diffs
            ;;
        get-session-status)
            action_get_session_status
            ;;
        delete-session)
            action_delete_session
            ;;
        update-session-tags)
            action_update_session_tags
            ;;
        get-session-delegations)
            action_get_session_delegations
            ;;
        browse-session-files)
            action_browse_session_files
            ;;
        get-session-file-content)
            action_get_session_file_content
            ;;
        search-session-files)
            action_search_session_files
            ;;
        get-session-models)
            action_get_session_models
            ;;
        get-session-agents)
            action_get_session_agents
            ;;
        create-session)
            action_create_session
            ;;
        prompt-session)
            action_prompt_session
            ;;
        stop-session)
            action_stop_session
            ;;
        abort-session)
            action_abort_session
            ;;
        resume-session)
            action_resume_session
            ;;
        fork-session)
            action_fork_session
            ;;
        add-session-source)
            action_add_session_source
            ;;
        preview-session-source)
            action_preview_session_source
            ;;
        create-session-from-github-pr)
            action_create_session_from_github_pr
            ;;
        create-session-from-github-issue)
            action_create_session_from_github_issue
            ;;
        list-automations)
            action_list_automations
            ;;
        get-automation)
            action_get_automation
            ;;
        create-automation)
            action_create_automation
            ;;
        update-automation)
            action_update_automation
            ;;
        delete-automation)
            action_delete_automation
            ;;
        enable-automation)
            action_enable_automation
            ;;
        disable-automation)
            action_disable_automation
            ;;
        run-automation)
            action_run_automation
            ;;
        list-github-repos)
            action_list_github_repos
            ;;
        list-github-pulls)
            action_list_github_pulls
            ;;
        list-github-issues)
            action_list_github_issues
            ;;
        get-github-issue)
            action_get_github_issue
            ;;
        get-github-pr)
            action_get_github_pr
            ;;
        list-github-pr-comments)
            action_list_github_pr_comments
            ;;
        list-github-issue-comments)
            action_list_github_issue_comments
            ;;
        search-github-issues)
            action_search_github_issues
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            error "Unknown action: $action"
            ;;
    esac
}

main "$@"
