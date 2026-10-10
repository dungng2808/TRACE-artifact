using System;
using System.Collections.Generic;
using System.IO;
using FullChain.Contracts;
using RQ2Experiment.Core.Domain;

namespace FullChain.Infrastructure.Rq2;

public static class ExperimentTaskFactory
{
    public static ExperimentTask CreateTask(
        TargetDescriptor target,
        string workspacePath,
        string testRelativePath,
        string testClassName,
        string failingTestMethod,
        string scenarioId,
        string scenarioSpecification,
        string traceabilityLink,
        string failureEvidence,
        string javaHome)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(workspacePath);

        // Derive relative focal source path if possible
        var focalSources = new List<string>();
        if (!string.IsNullOrWhiteSpace(target.SourceFile))
        {
            focalSources.Add(target.SourceFile);
        }
        else
        {
            // Default Maven convention: src/main/java/{FocalClass}.java
            var relativeFocalPath = Path.Combine("src", "main", "java", $"{target.FocalClass.Replace('.', '/')}.java");
            focalSources.Add(relativeFocalPath);
        }

        var taskId = $"{target.SubjectId}_{scenarioId}_{failingTestMethod}";

        return new ExperimentTask(
            TaskId: taskId,
            RepositoryPath: workspacePath,
            ScenarioId: scenarioId,
            TestId: testClassName,
            FailingTestMethod: failingTestMethod,
            ScenarioSpecification: scenarioSpecification,
            TraceabilityLink: traceabilityLink,
            InitialEvidence: failureEvidence,
            TestFilePath: testRelativePath,
            TestClassName: testClassName,
            ModulePath: "",
            BuildSystem: "maven",
            FocalSourceFiles: focalSources,
            PreviouslyVerifiedTests: null,
            CoverageReportPath: "target/site/jacoco/jacoco.xml",
            MutationReportPath: "target/pit-reports/mutations.xml",
            BuggyRepositoryPath: "",
            ProjectId: target.ProjectId,
            DatasetId: "sf110",
            JavaHome: javaHome
        );
    }
}
