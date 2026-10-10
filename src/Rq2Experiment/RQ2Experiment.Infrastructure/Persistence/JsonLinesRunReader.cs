using System.Text.Json;
using System.Text.Json.Serialization;
using RQ2Experiment.Core.Domain;

namespace RQ2Experiment.Infrastructure.Persistence;

public static class JsonLinesRunReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<IReadOnlyList<ExperimentRunResult>> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var absolutePath = Path.GetFullPath(path);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException("runs.jsonl was not found.", absolutePath);
        }

        var results = new List<ExperimentRunResult>();
        var lineNumber = 0;
        await foreach (var line in File.ReadLinesAsync(absolutePath, cancellationToken))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                results.Add(ExperimentRunResultSchemaMigrator.Deserialize(line, JsonOptions));
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    $"Invalid runs.jsonl record at line {lineNumber}: {exception.Message}", exception);
            }
        }

        if (results.Count == 0)
        {
            throw new InvalidOperationException("runs.jsonl contains no results.");
        }

        return results;
    }
}
