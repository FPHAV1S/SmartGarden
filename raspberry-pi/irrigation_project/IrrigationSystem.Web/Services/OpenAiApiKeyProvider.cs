namespace IrrigationSystem.Web.Services;

public sealed class OpenAiApiKeyProvider
{
    private readonly IConfiguration Configuration;

    public OpenAiApiKeyProvider(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public OpenAiApiKeyLookup GetApiKey(string? environmentVariableName)
    {
        var variableName = string.IsNullOrWhiteSpace(environmentVariableName)
            ? "OPENAI_API_KEY"
            : environmentVariableName.Trim();

        var environmentValue = Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return new OpenAiApiKeyLookup(
                environmentValue,
                $"environment variable {variableName}",
                true);
        }

        var configuredValue = Configuration["OpenAI:ApiKey"];
        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            return new OpenAiApiKeyLookup(
                configuredValue,
                "user secret OpenAI:ApiKey",
                true);
        }

        return new OpenAiApiKeyLookup(
            null,
            $"environment variable {variableName} or user secret OpenAI:ApiKey",
            false);
    }
}

public sealed record OpenAiApiKeyLookup(string? ApiKey, string Source, bool IsConfigured);
