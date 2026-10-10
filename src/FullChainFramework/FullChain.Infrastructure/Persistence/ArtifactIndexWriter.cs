using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FullChain.Contracts;

namespace FullChain.Infrastructure.Persistence;

public sealed class ArtifactIndexWriter : IArtifactIndexWriter
{
    private readonly string _baseRunsDirectory;
    private readonly ConcurrentDictionary<string, List<ArtifactIndexEntry>> _indices = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ArtifactIndexWriter(string baseRunsDirectory)
    {
        _baseRunsDirectory = baseRunsDirectory ?? throw new ArgumentNullException(nameof(baseRunsDirectory));
    }

    public async Task RecordArtifactAsync(
        string campaignId,
        string targetId,
        int repetitionId,
        string treatment,
        string stage,
        string artifactType,
        string filePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        string fullPath = Path.GetFullPath(filePath);
        string relativePath = Path.GetRelativePath(_baseRunsDirectory, fullPath);

        long byteSize = new FileInfo(fullPath).Length;
        string sha256 = await ComputeSha256Async(fullPath, ct);

        var entry = new ArtifactIndexEntry(
            ArtifactType: artifactType,
            Stage: stage,
            RelativePath: relativePath,
            AbsolutePath: fullPath,
            Sha256: sha256,
            ByteSize: byteSize,
            TimestampUtc: DateTime.UtcNow.ToString("o")
        );

        string key = BuildKey(campaignId, targetId, repetitionId, treatment);
        lock (_lock)
        {
            if (!_indices.TryGetValue(key, out var list))
            {
                list = new List<ArtifactIndexEntry>();
                _indices[key] = list;
            }

            // Avoid duplicate entries for the exact same file path and stage
            int existingIndex = list.FindIndex(e => e.AbsolutePath == fullPath && e.Stage == stage);
            if (existingIndex >= 0)
            {
                list[existingIndex] = entry;
            }
            else
            {
                list.Add(entry);
            }
        }
    }

    public Task<ArtifactIndex> GetIndexAsync(
        string campaignId,
        string targetId,
        int repetitionId,
        string treatment,
        CancellationToken ct = default)
    {
        string key = BuildKey(campaignId, targetId, repetitionId, treatment);
        lock (_lock)
        {
            var entries = _indices.TryGetValue(key, out var list)
                ? list.ToList()
                : new List<ArtifactIndexEntry>();

            var index = new ArtifactIndex(
                SchemaVersion: "full-chain-artifact-index-v1",
                CampaignId: campaignId,
                TargetId: targetId,
                RepetitionId: repetitionId,
                Treatment: treatment,
                Entries: entries
            );

            return Task.FromResult(index);
        }
    }

    public async Task SaveIndexAsync(
        string campaignId,
        string targetId,
        int repetitionId,
        string treatment,
        string outputPath,
        CancellationToken ct = default)
    {
        var index = await GetIndexAsync(campaignId, targetId, repetitionId, treatment, ct);
        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(index, JsonOptions);
        await File.WriteAllTextAsync(outputPath, json, ct);
    }

    private static string BuildKey(string campaignId, string targetId, int repetitionId, string treatment)
    {
        return $"{campaignId}:{treatment}:{targetId}:{repetitionId}";
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        byte[] hash = await sha256.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

