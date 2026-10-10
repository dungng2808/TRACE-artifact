using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Rq1Experiment.Contracts.Experiments;
using Rq1Experiment.Contracts.Subjects;
using Rq1Experiment.Core.Abstractions;
using Rq1Experiment.Infrastructure.Persistence;

namespace Rq1Experiment.Infrastructure.Java;

public sealed class GatedToolExecutionService(IProcessRunner processRunner) : IToolExecutionService
{
    private static readonly HashSet<string> ExcludedCopyDirectories = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".gradle", ".idea", ".br-analyzer", "target", "build", "bin", "obj" };

    public async Task<ToolExecutionReport> RunCompileAndTestAsync(
        ToolExecutionRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var subjects = await LoadSubjectsAsync(request.ExperimentOutputRoot, cancellationToken);
        var artifacts = DiscoverGenerationArtifacts(request).ToList();
        var records = new ConcurrentBag<ToolRunRecord>();
        var sequence = 0;
        await Parallel.ForEachAsync(artifacts, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(request.MaxParallelToolRuns, 1, 8),
            CancellationToken = cancellationToken
        }, async (artifact, _) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentSequence = Interlocked.Increment(ref sequence);

            var existingToolRunPath = Path.Combine(artifact.RunDirectory, "tools", "tool-run.json");
            if (File.Exists(existingToolRunPath))
            {
                var existingRecord = await JsonSupport.ReadAsync<ToolRunRecord>(existingToolRunPath, cancellationToken);
                if (existingRecord is not null && existingRecord.CompileTestCompletedAt != default)
                {
                    records.Add(existingRecord);
                    progress?.Report($"[{currentSequence}/{artifacts.Count}] Reusing immutable compile/test artifact {artifact.SubjectId} {artifact.Approach} R{artifact.Repetition:00}: {existingRecord.Status}.");
                    return;
                }
            }

            progress?.Report($"[{currentSequence}/{artifacts.Count}] Compile/JUnit {artifact.SubjectId} {artifact.Approach} R{artifact.Repetition:00}...");
            if (!subjects.TryGetValue(artifact.SubjectId, out var subject))
            {
                records.Add(await PersistTerminalAsync(request, artifact, null, ToolRunStatuses.GenerationInvalid,
                    "DISCOVERY", "Frozen subject was not found.", cancellationToken));
                return;
            }

            var record = NewRecord(request, artifact, subject);
            if (!string.Equals(artifact.GenerationStatus, "VALID", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(artifact.JavaFile))
            {
                record.Status = ToolRunStatuses.GenerationInvalid;
                record.FailureGate = "GENERATION";
                record.FailureReason = "Generation artifact is not VALID or has no generated Java source.";
                await PersistAsync(record, cancellationToken);
                records.Add(record);
                return;
            }

            try
            {
                await PrepareWorkspaceAsync(request, artifact, subject, record, cancellationToken);
                var build = DetectBuild(record.IsolatedWorkspace, request.WorkspaceRoot);
                if (build is null)
                {
                    record.Status = ToolRunStatuses.CompileFail;
                    record.FailureGate = "COMPILE";
                    record.FailureReason = "No Maven/Gradle build was found in isolated workspace.";
                    await FinishCompileAsync(record, cancellationToken);
                    Cleanup(record);
                    await PersistAsync(record, cancellationToken);
                    records.Add(record);
                    return;
                }
                record.BuildTool = build.Id;

                var compileArgs = build.Id == "maven"
                    ? new[] { "-B", "-ntp", "-DskipTests", "test-compile" }
                    : new[] { "--no-daemon", "testClasses" };
                var compile = await RunBuildAsync(record, build, compileArgs,
                    TimeSpan.FromSeconds(request.CompileTimeoutSeconds), "compile", request.JavaHome, cancellationToken);
                if (!compile.IsSuccess)
                {
                    record.Status = compile.TimedOut ? ToolRunStatuses.Timeout : ToolRunStatuses.CompileFail;
                    record.FailureGate = "COMPILE";
                    record.FailureReason = compile.TimedOut ? "Compile timed out." : "Generated test did not compile.";
                    await FinishCompileAsync(record, cancellationToken);
                    Cleanup(record);
                    await PersistAsync(record, cancellationToken);
                    records.Add(record);
                    return;
                }
                record.Status = ToolRunStatuses.CompilePass;

                var testArgs = build.Id == "maven"
                    ? new[] { "-B", "-ntp", $"-Dtest={record.GeneratedTestClass}", "-Dsurefire.failIfNoSpecifiedTests=true", "test" }
                    : new[] { "--no-daemon", "test", "--tests", record.GeneratedTestClass };
                var test = await RunBuildAsync(record, build, testArgs,
                    TimeSpan.FromSeconds(request.TestTimeoutSeconds), "junit", request.JavaHome, cancellationToken);
                var junit = await ReadAndRetainJUnitEvidenceAsync(record, cancellationToken);
                record.JunitTests = junit.Tests;
                record.JunitFailures = junit.Failures;
                record.JunitErrors = junit.Errors;
                record.JunitSkipped = junit.Skipped;
                await JsonSupport.WriteAsync(Path.Combine(record.GenerationRunDirectory, "tools", "junit-summary.json"), junit, cancellationToken);
                if (test.TimedOut)
                {
                    record.Status = ToolRunStatuses.Timeout;
                    record.FailureGate = "JUNIT";
                    record.FailureReason = "JUnit execution timed out.";
                }
                else if (junit.Tests == 0 || junit.Tests == junit.Skipped)
                {
                    record.Status = ToolRunStatuses.TestDiscoveryFail;
                    record.FailureGate = "JUNIT";
                    record.FailureReason = junit.Tests == 0
                        ? "The generated test class was not discovered."
                        : "JUnit discovered only skipped tests; no generated test executed.";
                }
                else if (!test.IsSuccess || junit.Failures > 0 || junit.Errors > 0)
                {
                    record.Status = junit.Failures > 0
                        ? ToolRunStatuses.AssertionFail
                        : junit.Errors > 0 ? ToolRunStatuses.RuntimeError : ToolRunStatuses.TestExecutionFail;
                    record.FailureGate = "JUNIT";
                    record.FailureReason = $"JUnit failed: tests={junit.Tests}, failures={junit.Failures}, errors={junit.Errors}.";
                }
                else
                {
                    record.Status = ToolRunStatuses.TestExecutionPass;
                    record.FailureGate = null;
                    record.FailureReason = null;
                    record.WorkspaceRetained = true;
                }
                await FinishCompileAsync(record, cancellationToken);
                if (record.Status != ToolRunStatuses.TestExecutionPass)
                {
                    Cleanup(record);
                    await PersistAsync(record, cancellationToken);
                }
                records.Add(record);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                record.Status = ToolRunStatuses.CompileFail;
                record.FailureGate = "INFRASTRUCTURE";
                record.FailureReason = exception.Message;
                await FinishCompileAsync(record, CancellationToken.None);
                Cleanup(record);
                await PersistAsync(record, CancellationToken.None);
                records.Add(record);
            }
        });

        var orderedRecords = records
            .OrderBy(record => record.SubjectId, StringComparer.Ordinal)
            .ThenBy(record => record.Approach, StringComparer.Ordinal)
            .ThenBy(record => record.Repetition)
            .ToList();

        var report = CreateReport(request, "COMPILE_JUNIT", orderedRecords);
        report.Complete = artifacts.Count > 0 && orderedRecords.Count == artifacts.Count;
        report.GeneratedTestsOnly = orderedRecords.Where(r => r.GenerationStatus == "VALID")
            .All(r => r.RemovedExistingTestFileCount >= 0);
        report.GateOrderingValid = orderedRecords.All(r =>
            r.Status is ToolRunStatuses.TestExecutionPass or ToolRunStatuses.CoveragePass or ToolRunStatuses.MutationPass or ToolRunStatuses.CoverageToolError or ToolRunStatuses.MutationToolError
            || !r.Processes.Any(p => p.Gate is "JACOCO" or "PIT"));
        await PersistReportAsync(request, "compile-test-report.json", orderedRecords, report, cancellationToken);
        return report;
    }

    public async Task<ToolExecutionReport> RunAdequacyAsync(
        ToolExecutionRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var records = await LoadToolRecordsAsync(request, cancellationToken);
        var eligible = records.Where(r => r.Status == ToolRunStatuses.TestExecutionPass).ToList();
        var sequence = 0;
        await Parallel.ForEachAsync(eligible, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(request.MaxParallelToolRuns, 1, 8),
            CancellationToken = cancellationToken
        }, async (record, _) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentSequence = Interlocked.Increment(ref sequence);

            if (record.AdequacyCompletedAt != default && record.Coverage is not null && record.Mutation is not null)
            {
                progress?.Report($"[{currentSequence}/{eligible.Count}] Reusing immutable adequacy artifact {record.SubjectId} {record.Approach} R{record.Repetition:00}: coverage & mutations valid.");
                return;
            }

            progress?.Report($"[{currentSequence}/{eligible.Count}] JaCoCo/PIT {record.SubjectId} {record.Approach} R{record.Repetition:00}...");
            var build = DetectBuild(record.IsolatedWorkspace, request.WorkspaceRoot);
            if (build is null || !Directory.Exists(record.IsolatedWorkspace))
            {
                record.Status = ToolRunStatuses.CoverageToolError;
                record.FailureGate = "JACOCO";
                record.FailureReason = "Isolated workspace required by adequacy stage is missing.";
                record.AdequacyCompletedAt = DateTimeOffset.UtcNow;
                await PersistAsync(record, cancellationToken);
                return;
            }

            try
            {
                var jacocoArgs = build.Id == "maven"
                    ? new[] { "-B", "-ntp", "clean", $"-Dtest={record.GeneratedTestClass}", "-Dsurefire.failIfNoSpecifiedTests=true", "test", $"org.jacoco:jacoco-maven-plugin:{request.JacocoVersion}:report" }
                    : new[] { "--no-daemon", "clean", "test", "--tests", record.GeneratedTestClass, "jacocoTestReport" };
                var jacoco = await RunBuildAsync(record, build, jacocoArgs,
                    TimeSpan.FromSeconds(request.JacocoTimeoutSeconds), "jacoco", request.JavaHome, cancellationToken);
                if (jacoco.IsSuccess)
                {
                    record.Coverage = await RetainAndParseCoverageAsync(record, cancellationToken);
                    if (record.Coverage is null || !record.Coverage.MethodFound)
                    {
                        record.Status = ToolRunStatuses.CoverageToolError;
                        record.FailureGate = "JACOCO";
                        record.FailureReason = "JaCoCo XML did not contain the focal method.";
                    }
                    else record.Status = ToolRunStatuses.CoveragePass;
                }
                else
                {
                    record.Status = jacoco.TimedOut ? ToolRunStatuses.Timeout : ToolRunStatuses.CoverageToolError;
                    record.FailureGate = "JACOCO";
                    record.FailureReason = jacoco.TimedOut ? "JaCoCo timed out." : "JaCoCo execution failed.";
                }

                var pitArgs = build.Id == "maven"
                    ? new[] { "-B", "-ntp", "test-compile", $"org.pitest:pitest-maven:{request.PitVersion}:mutationCoverage", $"-DtargetClasses={record.FocalClass}", $"-DtargetTests={record.GeneratedTestClass}", "-DfailWhenNoMutations=false", "-DoutputFormats=XML" }
                    : new[] { "--no-daemon", "pitest", $"-PtargetClasses={record.FocalClass}", $"-PtargetTests={record.GeneratedTestClass}" };
                var pit = await RunBuildAsync(record, build, pitArgs,
                    TimeSpan.FromSeconds(request.PitTimeoutSeconds), "pit", request.JavaHome, cancellationToken);
                if (pit.IsSuccess)
                {
                    record.Mutation = await RetainAndParseMutationsAsync(record, cancellationToken);
                    if (record.Mutation is null)
                    {
                        record.Status = ToolRunStatuses.MutationToolError;
                        record.FailureGate = "PIT";
                        record.FailureReason = "PIT did not produce mutations.xml.";
                    }
                    else if (record.Status == ToolRunStatuses.CoveragePass)
                    {
                        record.Status = ToolRunStatuses.MutationPass;
                        record.FailureGate = null;
                        record.FailureReason = null;
                    }
                }
                else
                {
                    record.Status = pit.TimedOut ? ToolRunStatuses.Timeout : ToolRunStatuses.MutationToolError;
                    record.FailureGate = "PIT";
                    record.FailureReason = pit.TimedOut ? "PIT timed out." : "PIT execution failed.";
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                record.Status = ToolRunStatuses.MutationToolError;
                record.FailureGate = "INFRASTRUCTURE";
                record.FailureReason = exception.Message;
            }
            finally
            {
                record.AdequacyCompletedAt = DateTimeOffset.UtcNow;
                await PersistAsync(record, CancellationToken.None);
                Cleanup(record);
                await PersistAsync(record, CancellationToken.None);
            }
        });

        var report = CreateReport(request, "JACOCO_PIT", records);
        report.GeneratedTestsOnly = records.All(HasGeneratedTestFilters);
        report.GateOrderingValid = records.All(r =>
            r.Processes.All(p => p.Gate is not ("JACOCO" or "PIT"))
            || r.CompileTestCompletedAt is not null);
        report.Complete = records.Count > 0 && eligible.All(r => r.AdequacyCompletedAt is not null)
                          && records.All(r => r.Status != ToolRunStatuses.Pending);
        await PersistReportAsync(request, "adequacy-report.json", records, report, cancellationToken);
        return report;
    }

    private async Task PrepareWorkspaceAsync(ToolExecutionRequest request, GenerationArtifact artifact,
        FocalMethodSubject subject, ToolRunRecord record, CancellationToken token)
    {
        var repository = WorkspaceLayout.ResolveRepositoryPath(request.WorkspaceRoot, subject.RepositoryPath);
        if (!Directory.Exists(repository)) throw new DirectoryNotFoundException(repository);
        record.RepositoryPath = Path.GetFullPath(repository);
        var workspace = Path.Combine(request.ExperimentOutputRoot, "tool-workspaces", request.ExperimentId,
            subject.SubjectId, artifact.Approach, $"R{artifact.Repetition:00}");
        if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        Directory.CreateDirectory(workspace);
        CopyDirectory(repository, workspace, token);
        record.IsolatedWorkspace = workspace;
        record.WorkspaceRetained = true;

        var moduleRoot = subject.ModulePath == "." ? workspace : Path.Combine(workspace, subject.ModulePath);
        var existingTests = Path.Combine(moduleRoot, "src", "test");
        record.RemovedExistingTestFileCount = Directory.Exists(existingTests)
            ? Directory.EnumerateFiles(existingTests, "*.java", SearchOption.AllDirectories).Count()
            : 0;
        if (Directory.Exists(existingTests)) Directory.Delete(existingTests, recursive: true);

        var java = await File.ReadAllTextAsync(artifact.JavaFile!, token);
        var identity = ParseJavaIdentity(java);
        record.GeneratedTestClass = identity.FullyQualifiedClass;
        record.GeneratedTestSourceHash = Sha256(java);
        var testRoot = Path.Combine(moduleRoot, "src", "test", "java");
        var packageDirectory = string.IsNullOrWhiteSpace(identity.Package)
            ? testRoot
            : Path.Combine(testRoot, identity.Package.Replace('.', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(packageDirectory);
        await File.WriteAllTextAsync(Path.Combine(packageDirectory, identity.ClassName + ".java"), java, token);
        await InstrumentBuildAsync(record, moduleRoot, request.JacocoVersion, request.PitVersion, token);
    }

    private static async Task InstrumentBuildAsync(ToolRunRecord record, string moduleRoot,
        string jacocoVersion, string pitVersion, CancellationToken token)
    {
        var tools = Path.Combine(record.GenerationRunDirectory, "tools", "instrumentation");
        Directory.CreateDirectory(tools);
        var pom = Path.Combine(moduleRoot, "pom.xml");
        if (!File.Exists(pom))
        {
            await JsonSupport.WriteAsync(Path.Combine(tools, "manifest.json"), new
            {
                buildFile = "Gradle build retained unchanged",
                record.RemovedExistingTestFileCount,
                record.GeneratedTestSourceHash
            }, token);
            return;
        }

        var original = await File.ReadAllTextAsync(pom, token);
        var document = XDocument.Parse(original, LoadOptions.PreserveWhitespace);
        var ns = document.Root!.Name.Namespace;
        var build = document.Root.Element(ns + "build") ?? new XElement(ns + "build");
        if (build.Parent is null) document.Root.Add(build);
        var plugins = build.Element(ns + "plugins") ?? new XElement(ns + "plugins");
        if (plugins.Parent is null) build.Add(plugins);
        var jacoco = plugins.Elements(ns + "plugin").FirstOrDefault(plugin =>
            plugin.Element(ns + "artifactId")?.Value == "jacoco-maven-plugin");
        if (jacoco is null)
        {
            jacoco = new XElement(ns + "plugin",
                new XElement(ns + "groupId", "org.jacoco"),
                new XElement(ns + "artifactId", "jacoco-maven-plugin"),
                new XElement(ns + "version", jacocoVersion));
            plugins.Add(jacoco);
        }
        else
        {
            var version = jacoco.Element(ns + "version");
            if (version is null) jacoco.AddFirst(new XElement(ns + "version", jacocoVersion));
            else version.Value = jacocoVersion;
        }
        // A benchmark POM may already declare jacoco-maven-plugin only for the
        // report goal. A version alone does not attach the agent, so Maven can
        // succeed while producing no jacoco.exec/XML. Always ensure the
        // prepare-agent execution exists, including for pre-existing plugins.
        var jacocoExecutions = jacoco.Element(ns + "executions");
        if (jacocoExecutions is null)
        {
            jacocoExecutions = new XElement(ns + "executions");
            jacoco.Add(jacocoExecutions);
        }
        var hasPrepareAgent = jacocoExecutions.Elements(ns + "execution")
            .SelectMany(execution => execution.Element(ns + "goals")?.Elements(ns + "goal") ?? [])
            .Any(goal => goal.Value.Equals("prepare-agent", StringComparison.OrdinalIgnoreCase));
        if (!hasPrepareAgent)
        {
            jacocoExecutions.Add(new XElement(ns + "execution",
                new XElement(ns + "id", "rq1-prepare-agent"),
                new XElement(ns + "goals", new XElement(ns + "goal", "prepare-agent"))));
        }
        var pit = plugins.Elements(ns + "plugin").FirstOrDefault(plugin =>
            plugin.Element(ns + "artifactId")?.Value == "pitest-maven");
        if (pit is null)
        {
            pit = new XElement(ns + "plugin",
                new XElement(ns + "groupId", "org.pitest"),
                new XElement(ns + "artifactId", "pitest-maven"),
                new XElement(ns + "version", pitVersion),
                new XElement(ns + "dependencies",
                    new XElement(ns + "dependency",
                        new XElement(ns + "groupId", "org.pitest"),
                        new XElement(ns + "artifactId", "pitest-junit5-plugin"),
                        new XElement(ns + "version", "1.2.3"))));
            plugins.Add(pit);
        }
        else
        {
            var version = pit.Element(ns + "version");
            if (version is null) pit.AddFirst(new XElement(ns + "version", pitVersion));
            else version.Value = pitVersion;
        }
        var instrumented = document.ToString(SaveOptions.DisableFormatting);
        await File.WriteAllTextAsync(pom, instrumented, token);
        await File.WriteAllTextAsync(Path.Combine(tools, "pom.original.xml"), original, token);
        await File.WriteAllTextAsync(Path.Combine(tools, "pom.instrumented.xml"), instrumented, token);
        await File.WriteAllTextAsync(Path.Combine(tools, "instrumentation.patch.txt"),
            $"Pinned org.jacoco:jacoco-maven-plugin to {jacocoVersion}; added/pinned org.pitest:pitest-maven {pitVersion} with org.pitest:pitest-junit5-plugin in the isolated workspace only.\n", token);
        await JsonSupport.WriteAsync(Path.Combine(tools, "manifest.json"), new
        {
            buildFile = "pom.xml",
            originalSha256 = Sha256(original),
            instrumentedSha256 = Sha256(instrumented),
            record.RemovedExistingTestFileCount,
            record.GeneratedTestSourceHash,
            productionSourceModified = false
        }, token);
    }

    private async Task<ProcessRunResult> RunBuildAsync(ToolRunRecord record, BuildTool build,
        IEnumerable<string> arguments, TimeSpan timeout, string gate, string javaHome, CancellationToken token)
    {
        var scoped = ApplyModuleScope(build, arguments, record.ModulePath);
        var invocation = BuildInvocation(build, scoped);
        var result = await processRunner.RunAsync(invocation.Executable, invocation.Arguments,
            record.IsolatedWorkspace, timeout, BuildJavaEnvironment(javaHome), token);
        var tools = Path.Combine(record.GenerationRunDirectory, "tools", gate);
        Directory.CreateDirectory(tools);
        var stdout = Path.Combine(tools, "stdout.log");
        var stderr = Path.Combine(tools, "stderr.log");
        await File.WriteAllTextAsync(stdout, result.StandardOutput, token);
        await File.WriteAllTextAsync(stderr, result.StandardError, token);
        var evidence = new ProcessEvidence
        {
            Gate = gate.ToUpperInvariant(), Executable = invocation.Executable,
            Arguments = invocation.Arguments.ToList(), ExitCode = result.ExitCode,
            TimedOut = result.TimedOut, DurationSeconds = result.Duration.TotalSeconds,
            StandardOutputFile = stdout, StandardErrorFile = stderr
        };
        record.Processes.Add(evidence);
        await JsonSupport.WriteAsync(Path.Combine(tools, "process.json"), evidence, token);
        return result;
    }

    private static async Task<JunitSummary> ReadAndRetainJUnitEvidenceAsync(
        ToolRunRecord record, CancellationToken token)
    {
        var moduleRoot = record.ModulePath == "." ? record.IsolatedWorkspace : Path.Combine(record.IsolatedWorkspace, record.ModulePath);
        var files = Directory.Exists(moduleRoot)
            ? Directory.EnumerateFiles(moduleRoot, "TEST-*.xml", SearchOption.AllDirectories).ToArray()
            : [];
        var tests = 0; var failures = 0; var errors = 0; var skipped = 0;
        var retained = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var root = XDocument.Load(file).Root;
                if (root is null) continue;
                tests += IntAttribute(root, "tests"); failures += IntAttribute(root, "failures");
                errors += IntAttribute(root, "errors"); skipped += IntAttribute(root, "skipped");
                var destination = Path.Combine(record.GenerationRunDirectory, "tools", "junit", "reports", Path.GetFileName(file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, true);
                retained.Add(destination);
            }
            catch { }
        }
        await Task.CompletedTask;
        token.ThrowIfCancellationRequested();
        return new JunitSummary(tests, failures, errors, skipped, retained);
    }

    private static async Task<FocalCoverageEvidence?> RetainAndParseCoverageAsync(ToolRunRecord record, CancellationToken token)
    {
        var reports = Directory.EnumerateFiles(record.IsolatedWorkspace, "jacoco*.xml", SearchOption.AllDirectories).ToArray();
        if (reports.Length == 0) return null;
        var source = reports.OrderByDescending(File.GetLastWriteTimeUtc).First();
        var destination = Path.Combine(record.GenerationRunDirectory, "tools", "jacoco", "jacoco.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, true);
        var document = XDocument.Load(destination);
        var className = record.FocalClass.Replace('.', '/');
        var methodName = record.FocalMethodSignature.Split('(', 2)[0];
        var methods = document.Descendants("class")
            .Where(element => element.Attribute("name")?.Value == className)
            .Elements("method")
            .Where(element => element.Attribute("name")?.Value == methodName)
            .Where(element =>
            {
                var line = IntAttribute(element, "line");
                return line == 0 || (line >= record.FocalStartLine && line <= record.FocalEndLine);
            }).ToList();
        var evidence = new FocalCoverageEvidence { MethodFound = methods.Count > 0, ReportFile = destination };
        foreach (var method in methods)
        foreach (var counter in method.Elements("counter"))
        {
            var missed = IntAttribute(counter, "missed"); var covered = IntAttribute(counter, "covered");
            switch (counter.Attribute("type")?.Value)
            {
                case "LINE": evidence.LineMissed += missed; evidence.LineCovered += covered; break;
                case "BRANCH": evidence.BranchMissed += missed; evidence.BranchCovered += covered; break;
                case "INSTRUCTION": evidence.InstructionMissed += missed; evidence.InstructionCovered += covered; break;
            }
        }
        await JsonSupport.WriteAsync(Path.Combine(record.GenerationRunDirectory, "tools", "jacoco", "focal-coverage.json"), evidence, token);
        return evidence;
    }

    private static async Task<FocalMutationEvidence?> RetainAndParseMutationsAsync(ToolRunRecord record, CancellationToken token)
    {
        var reports = Directory.EnumerateFiles(record.IsolatedWorkspace, "mutations.xml", SearchOption.AllDirectories).ToArray();
        if (reports.Length == 0) return null;
        var source = reports.OrderByDescending(File.GetLastWriteTimeUtc).First();
        var destination = Path.Combine(record.GenerationRunDirectory, "tools", "pit", "mutations.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, true);
        var methodName = record.FocalMethodSignature.Split('(', 2)[0];
        var mutations = XDocument.Load(destination).Descendants("mutation")
            .Where(mutation => mutation.Element("mutatedClass")?.Value == record.FocalClass)
            .Where(mutation => mutation.Element("mutatedMethod")?.Value == methodName)
            .Where(mutation =>
            {
                var line = int.TryParse(mutation.Element("lineNumber")?.Value, out var parsed) ? parsed : 0;
                return line >= record.FocalStartLine && line <= record.FocalEndLine;
            }).ToList();
        var evidence = new FocalMutationEvidence { FocalMutants = mutations.Count, ReportFile = destination };
        foreach (var mutation in mutations)
        {
            var status = mutation.Attribute("status")?.Value ?? mutation.Element("status")?.Value ?? string.Empty;
            switch (status.ToUpperInvariant())
            {
                case "KILLED": evidence.Killed++; break;
                case "SURVIVED": evidence.Survived++; break;
                case "NO_COVERAGE": evidence.NoCoverage++; break;
                case "NON_VIABLE": evidence.NonViable++; break;
                case "TIMED_OUT": evidence.TimedOut++; break;
                case "RUN_ERROR": evidence.RunError++; break;
                case "MEMORY_ERROR": evidence.MemoryError++; break;
            }
        }
        await JsonSupport.WriteAsync(Path.Combine(record.GenerationRunDirectory, "tools", "pit", "focal-mutations.json"), evidence, token);
        return evidence;
    }

    private static bool HasGeneratedTestFilters(ToolRunRecord record)
    {
        var jacoco = record.Processes.FirstOrDefault(p => p.Gate == "JACOCO");
        var pit = record.Processes.FirstOrDefault(p => p.Gate == "PIT");
        if (jacoco is null && pit is null) return true;
        return jacoco is not null && jacoco.Arguments.Any(a => a.Contains(record.GeneratedTestClass, StringComparison.Ordinal))
               && pit is not null
               && pit.Arguments.Any(a => a.Contains("targetTests", StringComparison.Ordinal) && a.Contains(record.GeneratedTestClass, StringComparison.Ordinal))
               && pit.Arguments.Any(a => a.Contains("targetClasses", StringComparison.Ordinal) && a.Contains(record.FocalClass, StringComparison.Ordinal));
    }

    private static ToolExecutionReport CreateReport(ToolExecutionRequest request, string stage, IReadOnlyCollection<ToolRunRecord> records) => new()
    {
        ExperimentId = request.ExperimentId, Mode = request.Mode, Stage = stage,
        DiscoveredRunCount = records.Count,
        GenerationInvalidCount = records.Count(r => r.Status == ToolRunStatuses.GenerationInvalid),
        CompilePassCount = records.Count(r => r.Processes.Any(p => p.Gate == "COMPILE" && !p.TimedOut && p.ExitCode == 0)),
        CompileFailCount = records.Count(r => r.Status == ToolRunStatuses.CompileFail),
        JunitProcessSuccessCount = records.Count(r => r.Processes.Any(p =>
            p.Gate == "JUNIT" && !p.TimedOut && p.ExitCode == 0)),
        ExecutionPassCount = records.Count(HasActuallyPassingJunit),
        ExecutionFailCount = records.Count(r => r.Status is ToolRunStatuses.TestDiscoveryFail or ToolRunStatuses.TestExecutionFail or ToolRunStatuses.AssertionFail or ToolRunStatuses.RuntimeError),
        TestDiscoveryFailCount = records.Count(r => r.Status == ToolRunStatuses.TestDiscoveryFail),
        CoveragePassCount = records.Count(r => r.Coverage?.MethodFound == true),
        CoverageToolErrorCount = records.Count(r => r.Status == ToolRunStatuses.CoverageToolError),
        MutationPassCount = records.Count(r => r.Mutation is not null),
        MutationToolErrorCount = records.Count(r => r.Status == ToolRunStatuses.MutationToolError),
        TimeoutCount = records.Count(r => r.Status == ToolRunStatuses.Timeout),
        GeneratedAt = DateTimeOffset.UtcNow
    };

    private static bool HasActuallyPassingJunit(ToolRunRecord record) =>
        record.Processes.Any(process => process.Gate == "JUNIT"
                                        && !process.TimedOut
                                        && process.ExitCode == 0)
        && record.JunitTests > 0
        && record.JunitTests > record.JunitSkipped
        && record.JunitFailures == 0
        && record.JunitErrors == 0;

    private static async Task PersistReportAsync(ToolExecutionRequest request, string name,
        IReadOnlyCollection<ToolRunRecord> records, ToolExecutionReport report, CancellationToken token)
    {
        await JsonSupport.WriteAsync(Path.Combine(request.ExperimentOutputRoot, name), report, token);
        await File.WriteAllTextAsync(Path.Combine(request.ExperimentOutputRoot, "tool-run-ledger.jsonl"),
            string.Join('\n', records.Select(r => JsonSerializer.Serialize(r, CompactJson))) + "\n", token);
    }

    private static readonly JsonSerializerOptions CompactJson = new(JsonSupport.Options) { WriteIndented = false };

    private static async Task<Dictionary<string, FocalMethodSubject>> LoadSubjectsAsync(string root, CancellationToken token)
    {
        var result = new Dictionary<string, FocalMethodSubject>(StringComparer.Ordinal);
        foreach (var name in new[] { "subjects-smoke.jsonl", "subjects-pilot.jsonl", "subjects-official.jsonl" })
        {
            var path = Path.Combine(root, "subjects", name);
            if (!File.Exists(path)) continue;
            foreach (var line in await File.ReadAllLinesAsync(path, token))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var subject = JsonSerializer.Deserialize<FocalMethodSubject>(line, JsonSupport.Options)!;
                result[subject.SubjectId] = subject;
            }
        }
        return result;
    }

    private static IEnumerable<GenerationArtifact> DiscoverGenerationArtifacts(ToolExecutionRequest request)
    {
        var root = Path.Combine(request.ExperimentOutputRoot, "runs", request.ExperimentId);
        if (!Directory.Exists(root)) yield break;
        foreach (var runJson in Directory.EnumerateFiles(root, "run.json", SearchOption.AllDirectories))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(runJson));
            var element = document.RootElement;
            if (!element.TryGetProperty("subjectId", out var subjectIdElement)
                || !element.TryGetProperty("approach", out var approachElement)) continue;
            var approach = approachElement.GetString() ?? string.Empty;
            if (approach is not ("DIRECT" or "PLANNED")) continue;
            var runDirectory = Path.GetDirectoryName(runJson)!;
            var repetition = element.TryGetProperty("repetition", out var repetitionElement) ? repetitionElement.GetInt32() : 0;
            var status = element.TryGetProperty("status", out var statusElement) ? statusElement.GetString() ?? "" : "";
            var javaCandidates = new[] { "generation", "generator" }
                .Select(name => Path.Combine(runDirectory, name))
                .Where(Directory.Exists)
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*.java"))
                .ToArray();
            var java = javaCandidates.Length == 1 ? javaCandidates[0] : null;
            yield return new GenerationArtifact(subjectIdElement.GetString()!, approach, repetition, status, runDirectory, java);
        }
    }

    private static async Task<List<ToolRunRecord>> LoadToolRecordsAsync(ToolExecutionRequest request, CancellationToken token)
    {
        var records = new List<ToolRunRecord>();
        foreach (var artifact in DiscoverGenerationArtifacts(request))
        {
            var path = Path.Combine(artifact.RunDirectory, "tools", "tool-run.json");
            if (!File.Exists(path)) continue;
            var record = await JsonSupport.ReadAsync<ToolRunRecord>(path, token);
            if (record is not null) records.Add(record);
        }
        return records;
    }

    private static ToolRunRecord NewRecord(ToolExecutionRequest request, GenerationArtifact artifact, FocalMethodSubject subject) => new()
    {
        ExperimentId = request.ExperimentId, SubjectId = subject.SubjectId, ProjectId = subject.ProjectId,
        Approach = artifact.Approach, Repetition = artifact.Repetition, GenerationRunDirectory = artifact.RunDirectory,
        GenerationStatus = artifact.GenerationStatus, ModulePath = subject.ModulePath, FocalClass = subject.FocalClass,
        FocalMethodSignature = subject.FocalMethodSignature, FocalStartLine = subject.StartLine,
        FocalEndLine = subject.EndLine, StartedAt = DateTimeOffset.UtcNow
    };

    private static async Task<ToolRunRecord> PersistTerminalAsync(ToolExecutionRequest request, GenerationArtifact artifact,
        FocalMethodSubject? subject, string status, string gate, string reason, CancellationToken token)
    {
        var record = subject is null
            ? new ToolRunRecord { ExperimentId = request.ExperimentId, SubjectId = artifact.SubjectId, Approach = artifact.Approach,
                Repetition = artifact.Repetition, GenerationRunDirectory = artifact.RunDirectory, GenerationStatus = artifact.GenerationStatus,
                StartedAt = DateTimeOffset.UtcNow }
            : NewRecord(request, artifact, subject);
        record.Status = status; record.FailureGate = gate; record.FailureReason = reason;
        record.CompileTestCompletedAt = DateTimeOffset.UtcNow;
        await PersistAsync(record, token); return record;
    }

    private static async Task FinishCompileAsync(ToolRunRecord record, CancellationToken token)
    { record.CompileTestCompletedAt = DateTimeOffset.UtcNow; await PersistAsync(record, token); }

    private static async Task PersistAsync(ToolRunRecord record, CancellationToken token) =>
        await JsonSupport.WriteAsync(Path.Combine(record.GenerationRunDirectory, "tools", "tool-run.json"), record, token);

    private static void Cleanup(ToolRunRecord record)
    {
        try { if (Directory.Exists(record.IsolatedWorkspace)) Directory.Delete(record.IsolatedWorkspace, true); }
        catch { return; }
        record.WorkspaceRetained = false;
    }

    private static void CopyDirectory(string source, string destination, CancellationToken token)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, directory);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(ExcludedCopyDirectories.Contains)) continue;
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(ExcludedCopyDirectories.Contains)) continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static BuildTool? DetectBuild(string repository, string anchor)
    {
        var mvnw = Path.Combine(repository, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw");
        if (File.Exists(mvnw)) return new("maven", mvnw);
        if (File.Exists(Path.Combine(repository, "pom.xml"))) return new("maven", BundledBuildToolResolver.ResolveMaven(anchor));
        var gradlew = Path.Combine(repository, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");
        if (File.Exists(gradlew)) return new("gradle", gradlew);
        if (File.Exists(Path.Combine(repository, "build.gradle")) || File.Exists(Path.Combine(repository, "build.gradle.kts")))
            return new("gradle", BundledBuildToolResolver.ResolveGradle(anchor));
        return null;
    }

    private static Invocation BuildInvocation(BuildTool build, IReadOnlyList<string> arguments)
    {
        if (OperatingSystem.IsWindows() && (build.Executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                                            || build.Executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
            return new("cmd.exe", ["/d", "/c", "call", build.Executable, .. arguments]);
        return new(build.Executable, arguments);
    }

    private static IReadOnlyList<string> ApplyModuleScope(BuildTool build, IEnumerable<string> arguments, string modulePath)
    {
        var values = arguments.ToArray();
        if (modulePath == ".") return values;
        if (build.Id == "maven") return ["-pl", modulePath.Replace('\\', '/'), "-am", .. values];
        var prefix = ":" + modulePath.Replace('\\', ':').Replace('/', ':').Trim(':');
        var tasks = new HashSet<string>(StringComparer.Ordinal)
        { "clean", "test", "testClasses", "jacocoTestReport", "pitest" };
        return values.Select(value => tasks.Contains(value) ? prefix + ":" + value : value).ToArray();
    }

    private static Dictionary<string, string?> BuildJavaEnvironment(string javaHome)
    {
        var values = new Dictionary<string, string?> { ["MAVEN_OPTS"] = "-Dfile.encoding=UTF-8", ["GRADLE_OPTS"] = "-Dfile.encoding=UTF-8" };
        if (string.IsNullOrWhiteSpace(javaHome)) return values;
        values["JAVA_HOME"] = javaHome;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        values["PATH"] = Path.Combine(javaHome, "bin") + Path.PathSeparator + path;
        return values;
    }

    private static JavaIdentity ParseJavaIdentity(string source)
    {
        var package = Regex.Match(source, @"(?m)^\s*package\s+([A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)\s*;").Groups[1].Value;
        var className = Regex.Match(source, @"\b(?:public\s+)?(?:final\s+)?class\s+([A-Za-z_$][\w$]*)").Groups[1].Value;
        if (string.IsNullOrWhiteSpace(className)) throw new InvalidDataException("Generated Java class declaration was not found.");
        return new(package, className, string.IsNullOrWhiteSpace(package) ? className : package + "." + className);
    }

    private static int IntAttribute(XElement element, string name) =>
        int.TryParse(element.Attribute(name)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record GenerationArtifact(string SubjectId, string Approach, int Repetition,
        string GenerationStatus, string RunDirectory, string? JavaFile);
    private sealed record BuildTool(string Id, string Executable);
    private sealed record Invocation(string Executable, IReadOnlyList<string> Arguments);
    private sealed record JavaIdentity(string Package, string ClassName, string FullyQualifiedClass);
    private sealed record JunitSummary(int Tests, int Failures, int Errors, int Skipped, List<string> ReportFiles);
}
