using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace FullChain.Infrastructure.Evaluation;

public sealed record JacocoFocalResult(
    double BranchCoverage,
    int CoveredBranches,
    int MissedBranches,
    IReadOnlyDictionary<int, int> LineCoveredBranches,
    IReadOnlyDictionary<int, int> LineMissedBranches,
    IReadOnlyDictionary<int, int> LineCoveredInstructions,
    IReadOnlyDictionary<int, int> LineMissedInstructions,
    string RawXml
);

public static class JacocoParser
{
    public static JacocoFocalResult ParseFocalCoverage(
        string jacocoXmlContent,
        string focalClassName,
        int startLine,
        int endLine)
    {
        if (string.IsNullOrWhiteSpace(jacocoXmlContent))
        {
            throw new ArgumentException("JaCoCo XML content is empty.", nameof(jacocoXmlContent));
        }

        var doc = XDocument.Parse(jacocoXmlContent);
        var simpleName = focalClassName.Split('.').Last().Split('$').First();
        var focalSourceFileName = $"{simpleName}.java";

        var lineCoveredBranches = new Dictionary<int, int>();
        var lineMissedBranches = new Dictionary<int, int>();
        var lineCoveredInstructions = new Dictionary<int, int>();
        var lineMissedInstructions = new Dictionary<int, int>();
        int coveredBranches = 0;
        int missedBranches = 0;
        bool foundAnyLines = false;

        // JaCoCo sourcefile elements are grouped by package. A package match
        // alone would include every sibling class (and every default-package
        // class), corrupting focal coverage. Match the focal source file
        // exactly; nested focal classes are represented by their outer source.
        var sourceFiles = doc.Descendants("sourcefile");
        foreach (var sf in sourceFiles)
        {
            var sfName = sf.Attribute("name")?.Value ?? "";
            if (sfName.Equals(focalSourceFileName, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in sf.Elements("line"))
                {
                    if (int.TryParse(line.Attribute("nr")?.Value, out int nr) && nr >= startLine && nr <= endLine)
                    {
                        foundAnyLines = true;
                        int cb = int.TryParse(line.Attribute("cb")?.Value, out int cVal) ? cVal : 0;
                        int mb = int.TryParse(line.Attribute("mb")?.Value, out int mVal) ? mVal : 0;
                        int ci = int.TryParse(line.Attribute("ci")?.Value, out int ciVal) ? ciVal : 0;
                        int mi = int.TryParse(line.Attribute("mi")?.Value, out int miVal) ? miVal : 0;

                        coveredBranches += cb;
                        missedBranches += mb;
                        lineCoveredBranches[nr] = cb;
                        lineMissedBranches[nr] = mb;
                        lineCoveredInstructions[nr] = ci;
                        lineMissedInstructions[nr] = mi;
                    }
                }
            }
        }

        double branchCoverage = 0.0;
        if (coveredBranches + missedBranches > 0)
        {
            branchCoverage = (double)coveredBranches / (coveredBranches + missedBranches) * 100.0;
        }
        else if (foundAnyLines)
        {
            // If method has no decision branches, covered instructions define branch coverage equivalence
            branchCoverage = 100.0;
        }

        return new JacocoFocalResult(
            BranchCoverage: branchCoverage,
            CoveredBranches: coveredBranches,
            MissedBranches: missedBranches,
            LineCoveredBranches: lineCoveredBranches,
            LineMissedBranches: lineMissedBranches,
            LineCoveredInstructions: lineCoveredInstructions,
            LineMissedInstructions: lineMissedInstructions,
            RawXml: jacocoXmlContent
        );
    }
}
