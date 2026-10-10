using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using FullChain.Contracts;

namespace FullChain.Infrastructure.Evaluation;

public sealed record PitFocalResult(
    double MutationScore,
    int TotalMutants,
    int KilledMutants,
    int SurvivedMutants,
    IReadOnlyList<MutationEvidence> FocalMutations,
    string RawXml
);

public static class PitParser
{
    public static PitFocalResult ParseFocalMutations(
        string pitXmlContent,
        string focalClassName,
        int startLine,
        int endLine)
    {
        if (string.IsNullOrWhiteSpace(pitXmlContent))
        {
            return new PitFocalResult(0.0, 0, 0, 0, Array.Empty<MutationEvidence>(), string.Empty);
        }

        var doc = XDocument.Parse(pitXmlContent);
        var simpleName = focalClassName.Split('.').Last().Split('$').First();

        int total = 0;
        int killed = 0;
        int survived = 0;
        var focalMutations = new List<MutationEvidence>();

        foreach (var mut in doc.Descendants("mutation"))
        {
            var mutClass = mut.Element("mutatedClass")?.Value ?? "";
            var lineStr = mut.Element("lineNumber")?.Value ?? "0";
            if (int.TryParse(lineStr, out int lineNr))
            {
                if ((mutClass.EndsWith(simpleName, StringComparison.OrdinalIgnoreCase) ||
                     mutClass.Equals(focalClassName, StringComparison.OrdinalIgnoreCase)) &&
                    lineNr >= startLine && lineNr <= endLine)
                {
                    total++;
                    var status = mut.Attribute("status")?.Value ?? mut.Element("status")?.Value ?? "";
                    var detected = mut.Attribute("detected")?.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
                    focalMutations.Add(new MutationEvidence(
                        Line: lineNr,
                        Mutator: mut.Element("mutator")?.Value ?? "",
                        Description: mut.Element("description")?.Value ?? "",
                        Status: status,
                        Detected: detected,
                        KillingTest: mut.Element("killingTest")?.Value));

                    if (detected || status.Equals("KILLED", StringComparison.OrdinalIgnoreCase) ||
                        status.Equals("TIMED_OUT", StringComparison.OrdinalIgnoreCase))
                    {
                        killed++;
                    }
                    else
                    {
                        survived++;
                    }
                }
            }
        }

        double score = total > 0 ? (double)killed / total * 100.0 : 0.0;

        return new PitFocalResult(
            MutationScore: score,
            TotalMutants: total,
            KilledMutants: killed,
            SurvivedMutants: survived,
            FocalMutations: focalMutations,
            RawXml: pitXmlContent
        );
    }
}
