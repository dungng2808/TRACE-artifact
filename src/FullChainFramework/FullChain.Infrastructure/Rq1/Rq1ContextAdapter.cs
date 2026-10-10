using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FullChain.Contracts;

namespace FullChain.Infrastructure.Rq1;

public sealed record Rq1ContextPackage(
    string SubjectId,
    string CommonContextJson,
    string StructuralInventoryJson,
    string PlannerSystemPrompt,
    string PlannerUserPrompt,
    string GeneratorSystemPrompt,
    string GeneratorUserPromptTemplate
);

public sealed class Rq1ContextAdapter
{
    private readonly string _repoRoot;
    private readonly string _contextsFile;
    private readonly string _targetsFile;

    public Rq1ContextAdapter(string repoRoot)
    {
        _repoRoot = repoRoot;
        _contextsFile = Path.Combine(repoRoot, "experiments", "rq1", "context", "contexts-official.jsonl");
        _targetsFile = Path.Combine(repoRoot, "experiments", "rq1", "targets", "targets-official.jsonl");
    }

    public async Task<Rq1ContextPackage> LoadContextAsync(TargetDescriptor target, CancellationToken ct = default)
    {
        // 1. Locate context line
        string? contextJson = null;
        if (File.Exists(_contextsFile))
        {
            using var reader = new StreamReader(_contextsFile);
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (line.Contains($"\"subjectId\":\"{target.SubjectId}\"", StringComparison.Ordinal))
                {
                    contextJson = line;
                    break;
                }
            }
        }

        if (contextJson == null)
        {
            throw new InvalidOperationException($"Context line not found for subject: {target.SubjectId} in {_contextsFile}");
        }

        // 2. Locate target inventory line
        string? targetInventoryJson = null;
        if (File.Exists(_targetsFile))
        {
            using var reader = new StreamReader(_targetsFile);
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (line.Contains($"\"subjectId\":\"{target.SubjectId}\"", StringComparison.Ordinal))
                {
                    targetInventoryJson = line;
                    break;
                }
            }
        }

        if (targetInventoryJson == null)
        {
            // Fallback to obligations from TargetDescriptor
            targetInventoryJson = JsonSerializer.Serialize(new
            {
                subjectId = target.SubjectId,
                projectId = target.ProjectId,
                focalClass = target.FocalClass,
                focalMethodSignature = target.FocalMethodSignature,
                sourceFile = target.SourceFile,
                sourceSha256 = target.SourceSha256,
                officialTargetSemantics = target.OfficialTargetSemantics,
                targets = target.Obligations
            });
        }

        // 3. Load prompt templates
        var promptsDir = Path.Combine(_repoRoot, "experiments", "rq1", "prompts");
        var plannerSys = await ReadFileSafeAsync(Path.Combine(promptsDir, "planner-system.md"), ct);
        var plannerUsrTmpl = await ReadFileSafeAsync(Path.Combine(promptsDir, "planner-user.md"), ct);
        var genSys = await ReadFileSafeAsync(Path.Combine(promptsDir, "generator-system.md"), ct);
        var genUsrTmpl = await ReadFileSafeAsync(Path.Combine(promptsDir, "generator-user.md"), ct);

        // Format planner user prompt
        var plannerUsr = plannerUsrTmpl
            .Replace("{{COMMON_CONTEXT_JSON}}", contextJson)
            .Replace("{{STRUCTURAL_TARGET_INVENTORY_JSON}}", targetInventoryJson)
            + "\nHARD_LIMIT: Generate no more than 3 scenarios.\n";

        return new Rq1ContextPackage(
            SubjectId: target.SubjectId,
            CommonContextJson: contextJson,
            StructuralInventoryJson: targetInventoryJson,
            PlannerSystemPrompt: plannerSys,
            PlannerUserPrompt: plannerUsr,
            GeneratorSystemPrompt: genSys,
            GeneratorUserPromptTemplate: genUsrTmpl
        );
    }

    private static async Task<string> ReadFileSafeAsync(string path, CancellationToken ct)
    {
        if (File.Exists(path))
        {
            return await File.ReadAllTextAsync(path, ct);
        }
        return string.Empty;
    }
}

