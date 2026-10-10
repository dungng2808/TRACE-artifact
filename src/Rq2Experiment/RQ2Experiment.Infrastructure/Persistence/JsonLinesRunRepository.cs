using System.Text.Json;
using System.Text.Json.Serialization;
using RQ2Experiment.Core.Abstractions;
using RQ2Experiment.Core.Domain;

namespace RQ2Experiment.Infrastructure.Persistence;

public sealed class JsonLinesRunRepository(string outputDirectory) : IRunRepository
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task SaveAsync(ExperimentRunResult result, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var filePath = Path.Combine(outputDirectory, "runs.jsonl");
        var line = JsonSerializer.Serialize(result, jsonOptions) + Environment.NewLine;

        await gate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(filePath, line, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }
}
