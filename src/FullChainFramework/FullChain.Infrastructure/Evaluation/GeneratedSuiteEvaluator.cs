using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using FullChain.Contracts;
using FullChain.Core.Evaluation;
using FullChain.Infrastructure.Evidence;
using FullChain.Infrastructure.Java;
using FullChain.Infrastructure.Processes;

namespace FullChain.Infrastructure.Evaluation;

public sealed class GeneratedSuiteEvaluator : ICommonEvaluator
{
    private readonly IProcessRunner _processRunner;
    private readonly ResolvedToolchain _toolchain;
    private readonly string _jacocoVersion;
    private readonly string _pitVersion;

    public GeneratedSuiteEvaluator(
        IProcessRunner processRunner,
        ResolvedToolchain toolchain,
        string jacocoVersion = "0.8.15",
        string pitVersion = "1.25.3")
    {
        _processRunner = processRunner;
        _toolchain = toolchain;
        _jacocoVersion = jacocoVersion;
        _pitVersion = pitVersion;
    }

    public async Task<CommonEnvelope> EvaluateSuiteAsync(
        TargetDescriptor target,
        string workspacePath,
        string testClassFqn,
        string testSourceCode,
        IGlobalBudgetGovernor budget,
        CancellationToken ct = default)
    {
        var parts = testClassFqn.Split('.');
        var testSimpleName = parts.Last();
        var evidenceDir = Path.Combine(workspacePath, "evaluation_evidence");
        Directory.CreateDirectory(evidenceDir);

        // 1. Ensure test file is in place in its package directory
        var relativeJavaPath = Path.Combine(parts) + ".java";
        var testFilePath = Path.Combine(workspacePath, "src", "test", "java", relativeJavaPath);
        var testFileDir = Path.GetDirectoryName(testFilePath);
        if (!string.IsNullOrEmpty(testFileDir))
        {
            Directory.CreateDirectory(testFileDir);
        }
        testSourceCode = testSourceCode.Replace("separateClassLoader = true", "separateClassLoader = false");
        await File.WriteAllTextAsync(testFilePath, testSourceCode, ct);


        // 2. Instrument isolated workspace POM with JaCoCo and PIT
        await InstrumentPomAsync(workspacePath, ct);

        var env = new Dictionary<string, string>
        {
            ["JAVA_HOME"] = _toolchain.JavaHome,
            ["JDK_JAVA_OPTIONS"] = JavaEnvironmentConstants.JdkJavaOptions
        };

        // 3. Run Maven clean test with JaCoCo prepare-agent and report
        budget.CheckBudget("EVALUATION_JACOCO");
        var jacocoArgs = $"-B -ntp clean org.jacoco:jacoco-maven-plugin:{_jacocoVersion}:prepare-agent test org.jacoco:jacoco-maven-plugin:{_jacocoVersion}:report -Dtest={testSimpleName} -Dmaven.test.failure.ignore=true";
        var jacocoTimeout = budget.GetRemainingStageTimeout("EVALUATION_JACOCO", TimeSpan.FromSeconds(120));

        var testRunResult = await _processRunner.RunAsync(
            _toolchain.MavenExecutable,
            jacocoArgs,
            workspacePath,
            env,
            jacocoTimeout,
            ct);

        await CommandEvidenceWriter.WriteAsync(evidenceDir, "jacoco_test_run", testRunResult, ct);

        bool compileSuccess = testRunResult.Stdout.Contains("T E S T S") || testRunResult.Stdout.Contains("Results:") || testRunResult.ExitCode == 0;
        bool testExecutionSuccess = testRunResult.ExitCode == 0 &&
                                    !testRunResult.Stdout.Contains("BUILD FAILURE") &&
                                    !testRunResult.Stdout.Contains("There are test failures") &&
                                    !Regex.IsMatch(testRunResult.Stdout, @"Failures:\s*[1-9]\d*") &&
                                    !Regex.IsMatch(testRunResult.Stdout, @"Errors:\s*[1-9]\d*");

        // 4. Locate and parse jacoco.xml
        var jacocoXmlFiles = Directory.EnumerateFiles(workspacePath, "jacoco*.xml", SearchOption.AllDirectories).ToArray();
        string jacocoXml = string.Empty;
        double branchCoverage = 0.0;
        var lineCoveredBranches = new Dictionary<int, int>();
        var lineMissedBranches = new Dictionary<int, int>();
        var lineCoveredInstructions = new Dictionary<int, int>();
        var lineMissedInstructions = new Dictionary<int, int>();

        if (jacocoXmlFiles.Length > 0)
        {
            jacocoXml = await File.ReadAllTextAsync(jacocoXmlFiles[0], ct);
            var jacocoParsed = JacocoParser.ParseFocalCoverage(jacocoXml, target.FocalClass, target.StartLine, target.EndLine);
            branchCoverage = jacocoParsed.BranchCoverage;
            foreach (var kvp in jacocoParsed.LineCoveredBranches)
            {
                lineCoveredBranches[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in jacocoParsed.LineMissedBranches)
            {
                lineMissedBranches[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in jacocoParsed.LineCoveredInstructions)
            {
                lineCoveredInstructions[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in jacocoParsed.LineMissedInstructions)
            {
                lineMissedInstructions[kvp.Key] = kvp.Value;
            }
        }

        // 5. Evaluate Structural Target Satisfaction (STS)
        var stsResult = StructuralTargetEvaluator.Evaluate(target.Obligations, lineCoveredBranches, lineCoveredInstructions);
        var obligationCoverage = target.Obligations
            .Select(obligation => new ObligationCoverageEvidence(
                TargetId: obligation.TargetId,
                Type: obligation.Type,
                Line: obligation.Line,
                CoveredBranches: lineCoveredBranches.TryGetValue(obligation.Line, out var coveredBranches) ? coveredBranches : 0,
                MissedBranches: lineMissedBranches.TryGetValue(obligation.Line, out var missedBranches) ? missedBranches : 0,
                CoveredInstructions: lineCoveredInstructions.TryGetValue(obligation.Line, out var coveredInstructions) ? coveredInstructions : 0,
                MissedInstructions: lineMissedInstructions.TryGetValue(obligation.Line, out var missedInstructions) ? missedInstructions : 0,
                Satisfied: stsResult.ObligationStatus.TryGetValue(obligation.TargetId, out var satisfied) && satisfied))
            .ToArray();

        // 6. Run PIT Mutation Analysis if tests pass
        string pitXml = string.Empty;
        double mutationScore = 0.0;
        IReadOnlyList<MutationEvidence> focalMutations = Array.Empty<MutationEvidence>();
        bool mutationEvidenceAvailable = false;

        if (testExecutionSuccess)
        {
            try
            {
                budget.CheckBudget("EVALUATION_PIT");
                // PIT resolves targetTests against fully-qualified test names.
                // A simple class name causes package-scoped generated tests to
                // execute zero tests and falsely report every focal mutation as
                // NO_COVERAGE, even when JaCoCo proves the test executed.
                var pitArgs = $"-B -ntp org.pitest:pitest-maven:{_pitVersion}:mutationCoverage -DtargetClasses={target.FocalClass}* -DtargetTests={testClassFqn}";
                var pitTimeout = budget.GetRemainingStageTimeout("EVALUATION_PIT", TimeSpan.FromSeconds(120));

                var pitResult = await _processRunner.RunAsync(
                    _toolchain.MavenExecutable,
                    pitArgs,
                    workspacePath,
                    env,
                    pitTimeout,
                    ct);

                await CommandEvidenceWriter.WriteAsync(evidenceDir, "pit_mutation_run", pitResult, ct);

                var pitXmlFiles = Directory.EnumerateFiles(workspacePath, "mutations*.xml", SearchOption.AllDirectories).ToArray();
                if (pitXmlFiles.Length > 0)
                {
                    pitXml = await File.ReadAllTextAsync(pitXmlFiles[0], ct);
                    var pitParsed = PitParser.ParseFocalMutations(pitXml, target.FocalClass, target.StartLine, target.EndLine);
                    mutationScore = pitParsed.MutationScore;
                    focalMutations = pitParsed.FocalMutations;
                    mutationEvidenceAvailable = true;
                }
            }
            catch (Exception)
            {
                // PIT failure treated as MissingEvidence or zero MS if tests failed
            }
        }

        var buildLogSnippet = ExtractLogSnippet(testRunResult.Stdout);
        var testSourceSha256 = ComputeSha256(testSourceCode);

        return new CommonEnvelope(
            SchemaVersion: "common-envelope-v1",
            TargetId: target.SubjectId,
            ProjectId: target.ProjectId,
            SourceSha256: target.SourceSha256,
            TestSourceSha256: testSourceSha256,
            TestSourceCode: testSourceCode,
            BranchObligations: target.Obligations,
            CompileSuccess: compileSuccess,
            TestExecutionSuccess: testExecutionSuccess,
            BuildLogSnippet: buildLogSnippet,
            BranchCoverage: branchCoverage,
            MutationScore: mutationScore,
            StructuralTargetSatisfaction: stsResult.SatisfactionPercentage,
            JaCoCoReportXml: jacocoXml,
            PitReportXml: pitXml,
            ObligationCoverage: obligationCoverage,
            FocalMutations: focalMutations,
            MutationEvidenceAvailable: mutationEvidenceAvailable
        );
    }

    private async Task InstrumentPomAsync(string workspacePath, CancellationToken ct)
    {
        var pomPath = Path.Combine(workspacePath, "pom.xml");
        if (!File.Exists(pomPath)) return;

        var original = await File.ReadAllTextAsync(pomPath, ct);
        var doc = XDocument.Parse(original, LoadOptions.PreserveWhitespace);
        var ns = doc.Root!.Name.Namespace;

        var build = doc.Root.Element(ns + "build") ?? new XElement(ns + "build");
        if (build.Parent == null) doc.Root.Add(build);

        var plugins = build.Element(ns + "plugins") ?? new XElement(ns + "plugins");
        if (plugins.Parent == null) build.Add(plugins);

        // Ensure JaCoCo plugin
        var jacoco = plugins.Elements(ns + "plugin").FirstOrDefault(p =>
            p.Element(ns + "artifactId")?.Value == "jacoco-maven-plugin");
        if (jacoco == null)
        {
            jacoco = new XElement(ns + "plugin",
                new XElement(ns + "groupId", "org.jacoco"),
                new XElement(ns + "artifactId", "jacoco-maven-plugin"),
                new XElement(ns + "version", _jacocoVersion));
            plugins.Add(jacoco);
        }

        var jacocoExecutions = jacoco.Element(ns + "executions");
        if (jacocoExecutions == null)
        {
            jacocoExecutions = new XElement(ns + "executions");
            jacoco.Add(jacocoExecutions);
        }

        var hasPrepareAgent = jacocoExecutions.Elements(ns + "execution")
            .SelectMany(e => e.Element(ns + "goals")?.Elements(ns + "goal") ?? [])
            .Any(g => g.Value.Equals("prepare-agent", StringComparison.OrdinalIgnoreCase));

        if (!hasPrepareAgent)
        {
            jacocoExecutions.Add(new XElement(ns + "execution",
                new XElement(ns + "id", "fullchain-prepare-agent"),
                new XElement(ns + "goals", new XElement(ns + "goal", "prepare-agent"))));
        }

        // Ensure PIT plugin
        var pit = plugins.Elements(ns + "plugin").FirstOrDefault(p =>
            p.Element(ns + "artifactId")?.Value == "pitest-maven");
        if (pit == null)
        {
            pit = new XElement(ns + "plugin",
                new XElement(ns + "groupId", "org.pitest"),
                new XElement(ns + "artifactId", "pitest-maven"),
                new XElement(ns + "version", _pitVersion),
                new XElement(ns + "dependencies",
                    new XElement(ns + "dependency",
                        new XElement(ns + "groupId", "org.pitest"),
                        new XElement(ns + "artifactId", "pitest-junit5-plugin"),
                        new XElement(ns + "version", "1.2.3"))));
            plugins.Add(pit);
        }

        // PIT's default report selection is environment/version dependent. B2
        // needs the focal mutation transformations, so make XML explicit and
        // keep it at a stable path for the evidence collector.
        var pitConfiguration = pit.Element(ns + "configuration");
        if (pitConfiguration == null)
        {
            pitConfiguration = new XElement(ns + "configuration");
            pit.Add(pitConfiguration);
        }

        var outputFormats = pitConfiguration.Element(ns + "outputFormats");
        if (outputFormats == null)
        {
            outputFormats = new XElement(ns + "outputFormats");
            pitConfiguration.Add(outputFormats);
        }

        var hasXmlOutput = outputFormats.Elements(ns + "param")
            .Any(param => param.Value.Equals("XML", StringComparison.OrdinalIgnoreCase));
        if (!hasXmlOutput)
        {
            outputFormats.Add(new XElement(ns + "param", "XML"));
        }

        if (pitConfiguration.Element(ns + "timestampedReports") == null)
        {
            pitConfiguration.Add(new XElement(ns + "timestampedReports", "false"));
        }

        await File.WriteAllTextAsync(pomPath, doc.ToString(SaveOptions.DisableFormatting), ct);
    }

    private static string ExtractLogSnippet(string stdout, int maxLines = 50)
    {
        var lines = stdout.Split('\n');
        if (lines.Length <= maxLines) return stdout;
        return string.Join('\n', lines.TakeLast(maxLines));
    }

    private static string ComputeSha256(string text)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }
}
