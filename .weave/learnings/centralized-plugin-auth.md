# Learnings: Centralized Plugin Auth

## Task 3: Clean GitHubEndpointMappings
- **Discrepancy**: Plan said to remove `using Microsoft.AspNetCore.Builder` since `WebApplication` is gone. But `MapGroup` is an extension method on `IEndpointRouteBuilder` defined in the `Microsoft.AspNetCore.Builder` namespace — so the using is still required.
- **Resolution**: Kept `using Microsoft.AspNetCore.Builder` alongside the new `using Microsoft.AspNetCore.Routing`.
- **Suggestion**: Plan should note that `MapGroup` (and other routing extensions like `MapGet`, `MapPost`) live in `Microsoft.AspNetCore.Builder` even though they extend `IEndpointRouteBuilder`.

## Task 6: Pre-existing test failure
- **Discrepancy**: `SkillEndpointPathTraversalTests.GetSkill_ReturnsBadRequestOrNotRouted_ForEncodedTraversal` was already failing before this plan — confirmed by stash+test.
- **Resolution**: Not addressed; unrelated to plugin auth. Noted as pre-existing.
- **Suggestion**: Plans should note pre-existing failing tests to avoid confusion during verification.
