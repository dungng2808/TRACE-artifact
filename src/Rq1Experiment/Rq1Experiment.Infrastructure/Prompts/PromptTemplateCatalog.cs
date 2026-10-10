namespace Rq1Experiment.Infrastructure.Prompts;

public sealed record PromptCatalogFile(string RelativePath, string Kind, string Content);

/// <summary>
/// Loads the version-controlled RQ1 prompt catalog used by both the experiment pipeline and the WPF UI.
/// </summary>
public static class PromptTemplateCatalog
{
    public const string SourceRelativePath =
        "src/Rq1Experiment/Rq1Experiment.Infrastructure/Prompts/Templates";

    private static readonly string[] TemplateFiles =
    [
        "direct-system.md",
        "direct-user.md",
        "planner-system.md",
        "planner-user.md",
        "generator-system.md",
        "generator-user.md"
    ];

    private static readonly string[] SchemaFiles =
    [
        "schemas/direct-output.schema.json",
        "schemas/scenario-plan.schema.json",
        "schemas/planned-generation-output.schema.json"
    ];

    public static IReadOnlyDictionary<string, string> LoadTemplates(string? workspaceRoot = null) =>
        TemplateFiles.ToDictionary(path => Path.GetFileName(path)!,
            path => Read(path, workspaceRoot), StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> LoadSchemas(string? workspaceRoot = null) =>
        SchemaFiles.ToDictionary(path => Path.GetFileName(path), path => Read(path, workspaceRoot), StringComparer.Ordinal);

    public static string LoadContract(string? workspaceRoot = null) =>
        Read("PROMPT_CONTRACT.md", workspaceRoot);

    public static IReadOnlyList<PromptCatalogFile> LoadAll(string? workspaceRoot = null)
    {
        var files = new List<PromptCatalogFile>();
        files.AddRange(TemplateFiles.Select(path =>
            new PromptCatalogFile(path, "Prompt template", Read(path, workspaceRoot))));
        files.Add(new PromptCatalogFile("PROMPT_CONTRACT.md", "Contract", LoadContract(workspaceRoot)));
        files.AddRange(SchemaFiles.Select(path =>
            new PromptCatalogFile(path, "JSON schema", Read(path, workspaceRoot))));
        return files;
    }

    public static string DescribeLocation(string? workspaceRoot = null)
    {
        var directory = ResolveDirectory(workspaceRoot);
        return directory ?? $"Embedded resources in {typeof(PromptTemplateCatalog).Assembly.GetName().Name}";
    }

    private static string Read(string relativePath, string? workspaceRoot)
    {
        var directory = ResolveDirectory(workspaceRoot);
        if (directory is not null)
        {
            var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                throw new FileNotFoundException($"Prompt catalog file is missing: {relativePath}", path);
            return Normalize(File.ReadAllText(path));
        }

        var assembly = typeof(PromptTemplateCatalog).Assembly;
        var suffix = ".Prompts.Templates." + relativePath.Replace('/', '.');
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal));
        if (resourceName is null)
            throw new FileNotFoundException($"Embedded prompt catalog file is missing: {relativePath}");
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new FileNotFoundException($"Cannot open embedded prompt catalog file: {relativePath}");
        using var reader = new StreamReader(stream);
        return Normalize(reader.ReadToEnd());
    }

    private static string? ResolveDirectory(string? workspaceRoot)
    {
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            var sourceDirectory = Path.Combine(Path.GetFullPath(workspaceRoot),
                SourceRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(sourceDirectory)) return sourceDirectory;
        }

        var deployedDirectory = Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates");
        return Directory.Exists(deployedDirectory) ? deployedDirectory : null;
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() + "\n";
}
