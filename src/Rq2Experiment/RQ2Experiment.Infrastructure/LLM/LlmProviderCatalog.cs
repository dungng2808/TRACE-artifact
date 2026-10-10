namespace RQ2Experiment.Infrastructure.LLM;

public enum LlmApiProtocol
{
    Responses,
    ChatCompletions
}

public sealed record LlmProviderPreset(
    string Id,
    string DisplayName,
    string BaseUrl,
    LlmApiProtocol Protocol,
    bool RequiresApiKey,
    bool UseJsonMode,
    string DefaultModel = "",
    string CredentialValidationPath = "");

public static class LlmProviderCatalog
{
    public static IReadOnlyList<LlmProviderPreset> All { get; } =
    [
        new("openai", "OpenAI", "https://api.openai.com/v1/", LlmApiProtocol.Responses, true, true, "gpt-5.6-terra"),
        new("openrouter", "OpenRouter (multi-provider)", "https://openrouter.ai/api/v1/", LlmApiProtocol.ChatCompletions, true, false, CredentialValidationPath: "key"),
        new("gemini", "Google Gemini (OpenAI-compatible)", "https://generativelanguage.googleapis.com/v1beta/openai/", LlmApiProtocol.ChatCompletions, true, true),
        new("anthropic", "Anthropic Claude (compatibility)", "https://api.anthropic.com/v1/", LlmApiProtocol.ChatCompletions, true, false),
        new("deepseek", "DeepSeek", "https://api.deepseek.com/", LlmApiProtocol.ChatCompletions, true, true, "deepseek-chat"),
        new("groq", "Groq", "https://api.groq.com/openai/v1/", LlmApiProtocol.ChatCompletions, true, false),
        new("together", "Together AI", "https://api.together.ai/v1/", LlmApiProtocol.ChatCompletions, true, true),
        new("ollama", "Ollama (local)", "http://localhost:11434/v1/", LlmApiProtocol.ChatCompletions, false, true),
        new("custom", "Custom OpenAI-compatible", "http://localhost:8000/v1/", LlmApiProtocol.ChatCompletions, false, false)
    ];
}
