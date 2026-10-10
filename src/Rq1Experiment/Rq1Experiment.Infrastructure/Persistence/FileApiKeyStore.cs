using Rq1Experiment.Contracts.Config;
using Rq1Experiment.Core.Abstractions;
using Rq1Experiment.Core.Config;

namespace Rq1Experiment.Infrastructure.Persistence;

public sealed class FileApiKeyStore(WorkspaceLayout layout) : IApiKeyStore
{
    public async Task<IReadOnlyList<string>> LoadAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(providerId);
        if (!File.Exists(path))
            return [];

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        return ApiKeyParser.Parse(text);
    }

    public async Task SaveAsync(
        string providerId,
        IEnumerable<string> keys,
        CancellationToken cancellationToken = default)
    {
        var normalized = ApiKeyParser.Parse(string.Join(Environment.NewLine, keys));
        Directory.CreateDirectory(layout.ApiKeyDirectory);
        await File.WriteAllTextAsync(
            GetPath(providerId),
            string.Join(Environment.NewLine, normalized),
            cancellationToken);
    }

    private string GetPath(string providerId)
    {
        var normalized = NormalizeProvider(providerId);
        return Path.Combine(layout.ApiKeyDirectory, normalized + ".txt");
    }

    private static string NormalizeProvider(string providerId)
    {
        var match = ProviderIds.All.FirstOrDefault(
            provider => provider.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new ArgumentException("Unsupported provider.", nameof(providerId));
    }
}
