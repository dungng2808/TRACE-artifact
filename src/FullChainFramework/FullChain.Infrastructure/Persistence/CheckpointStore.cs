using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FullChain.Contracts;

namespace FullChain.Infrastructure.Persistence;

public sealed class CheckpointStore : ICheckpointStore
{
    private readonly string _checkpointDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CheckpointStore(string checkpointDirectory)
    {
        _checkpointDirectory = checkpointDirectory ?? throw new ArgumentNullException(nameof(checkpointDirectory));
        Directory.CreateDirectory(_checkpointDirectory);
    }

    public async Task SaveCheckpointAsync(CampaignCheckpoint checkpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        string campaignDir = Path.Combine(_checkpointDirectory, checkpoint.CampaignId);
        Directory.CreateDirectory(campaignDir);

        string fileName = GetCheckpointFileName(checkpoint.Treatment, checkpoint.TargetId, checkpoint.RepetitionId);
        string filePath = Path.Combine(campaignDir, fileName);
        string tempPath = filePath + ".tmp";

        string json = JsonSerializer.Serialize(checkpoint, JsonOptions);

        await File.WriteAllTextAsync(tempPath, json, ct);

        File.Move(tempPath, filePath, overwrite: true);
    }

    public async Task<CampaignCheckpoint?> LoadCheckpointAsync(
        string campaignId,
        string targetId,
        int repetitionId,
        string treatment,
        CancellationToken ct = default)
    {
        string campaignDir = Path.Combine(_checkpointDirectory, campaignId);
        string fileName = GetCheckpointFileName(treatment, targetId, repetitionId);
        string filePath = Path.Combine(campaignDir, fileName);

        if (!File.Exists(filePath))
            return null;

        string json = await File.ReadAllTextAsync(filePath, ct);
        return JsonSerializer.Deserialize<CampaignCheckpoint>(json, JsonOptions);
    }

    public Task<bool> HasCheckpointAsync(
        string campaignId,
        string targetId,
        int repetitionId,
        string treatment,
        CancellationToken ct = default)
    {
        string campaignDir = Path.Combine(_checkpointDirectory, campaignId);
        string fileName = GetCheckpointFileName(treatment, targetId, repetitionId);
        string filePath = Path.Combine(campaignDir, fileName);

        return Task.FromResult(File.Exists(filePath));
    }

    public Task DeleteCheckpointAsync(
        string campaignId,
        string targetId,
        int repetitionId,
        string treatment,
        CancellationToken ct = default)
    {
        string campaignDir = Path.Combine(_checkpointDirectory, campaignId);
        string fileName = GetCheckpointFileName(treatment, targetId, repetitionId);
        string filePath = Path.Combine(campaignDir, fileName);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
        return Task.CompletedTask;
    }

    private static string GetCheckpointFileName(string treatment, string targetId, int repetitionId)
    {
        return $"{treatment}_{targetId}_rep{repetitionId}.checkpoint.json";
    }
}
