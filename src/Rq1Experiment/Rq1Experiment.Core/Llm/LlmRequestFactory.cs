using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rq1Experiment.Core.Abstractions;

namespace Rq1Experiment.Core.Llm;

public static class LlmRequestFactory
{
    public static LlmRequest Create(
        string providerId,
        string modelId,
        string role,
        string systemPrompt,
        string userPrompt)
    {
        var canonicalPayload = JsonSerializer.Serialize(new
        {
            providerId,
            modelId,
            role,
            systemPrompt,
            userPrompt
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload))).ToLowerInvariant();
        return new LlmRequest(providerId, modelId, role, systemPrompt, userPrompt, hash);
    }

    public static LlmRequest Configure(
        LlmRequest request,
        string baseEndpoint,
        double temperature,
        int maxOutputTokens,
        bool requireJsonObject = true,
        int transportRetryCount = 2,
        string deepSeekThinkingMode = "enabled")
    {
        var responseJsonSchema = requireJsonObject ? ResponseSchemaFor(request.Role) : string.Empty;
        var thinkingLevel = request.ProviderId.Equals("gemini", StringComparison.OrdinalIgnoreCase)
                            && request.ModelId.Contains("gemini-3", StringComparison.OrdinalIgnoreCase)
            ? "minimal" : string.Empty;
        var normalizedDeepSeekThinkingMode = (request.ProviderId.Equals("deepseek", StringComparison.OrdinalIgnoreCase)
                                              || request.ProviderId.Equals("teamorouter", StringComparison.OrdinalIgnoreCase))
            ? deepSeekThinkingMode.Trim().ToLowerInvariant()
            : string.Empty;
        var canonicalPayload = JsonSerializer.Serialize(new
        {
            request.ProviderId,
            request.ModelId,
            request.Role,
            request.SystemPrompt,
            request.UserPrompt,
            baseEndpoint,
            temperature,
            maxOutputTokens,
            requireJsonObject,
            responseJsonSchema,
            thinkingLevel,
            normalizedDeepSeekThinkingMode,
            transportRetryCount
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload))).ToLowerInvariant();
        return request with
        {
            RequestHash = hash,
            BaseEndpoint = baseEndpoint,
            Temperature = temperature,
            MaxOutputTokens = maxOutputTokens,
            RequireJsonObject = requireJsonObject,
            ResponseJsonSchema = responseJsonSchema,
            ThinkingLevel = thinkingLevel,
            DeepSeekThinkingMode = normalizedDeepSeekThinkingMode,
            TransportRetryCount = Math.Max(0, transportRetryCount)
        };
    }

    private static string ResponseSchemaFor(string role) => role switch
    {
        "DIRECT_GENERATOR" => """
            {"type":"object","additionalProperties":false,"required":["testClassName","sourceCode","declaredTestMethods"],"properties":{"testClassName":{"type":"string"},"sourceCode":{"type":"string"},"declaredTestMethods":{"type":"array","items":{"type":"string"}}}}
            """,
        "STRUCTURAL_PLANNER" => """
            {"type":"object","additionalProperties":false,"required":["subjectId","scenarios"],"properties":{"subjectId":{"type":"string"},"scenarios":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["scenarioId","technique","preconditions","concreteInput","expectedObservation","oracleProvenance","targetIds"],"properties":{"scenarioId":{"type":"string"},"technique":{"type":"string","enum":["PREDICATE_PARTITION","BOUNDARY_VALUE","BRANCH_OUTCOME","EXCEPTION_PATH"]},"preconditions":{"type":"array","items":{"type":"string"}},"concreteInput":{"type":"string"},"expectedObservation":{"type":"string"},"oracleProvenance":{"type":"object","additionalProperties":false,"required":["type","sourceLines"],"properties":{"type":{"type":"string"},"sourceLines":{"type":"array","items":{"type":"integer"}}}},"targetIds":{"type":"array","items":{"type":"string"}}}}}}}
            """,
        "PLANNED_GENERATOR" => """
            {"type":"object","additionalProperties":false,"required":["testClassName","sourceCode","declaredTestMethods","mappings"],"properties":{"testClassName":{"type":"string"},"sourceCode":{"type":"string"},"declaredTestMethods":{"type":"array","items":{"type":"string"}},"mappings":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["scenarioId","testClass","testMethod"],"properties":{"scenarioId":{"type":"string"},"testClass":{"type":"string"},"testMethod":{"type":"string"}}}}}}
            """,
        _ => string.Empty
    };
}
