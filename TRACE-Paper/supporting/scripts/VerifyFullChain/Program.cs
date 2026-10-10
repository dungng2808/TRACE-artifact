// Read-only recalculation using the workspace's original statistical engines.
// Candidate CSR/ESR are not in the input CSV and are deliberately not exported.
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using FullChain.Contracts;
using FullChain.Benchmark.Analysis;

if (args.Length != 2)
    throw new ArgumentException("Usage: VerifyFullChain <paired-comparison.csv> <output.json>");
using var parser = new TextFieldParser(args[0]);
parser.SetDelimiters(",");
parser.HasFieldsEnclosedInQuotes = true;
var header = parser.ReadFields()!;
var rows = new List<HierarchicalBootstrap.TargetRepetitionData>();
while (!parser.EndOfData)
{
    var fields = parser.ReadFields()!;
    var row = header.Select((k, i) => (k, v: fields[i])).ToDictionary(x => x.k, x => x.v);
    double N(string key) => double.Parse(row[key], CultureInfo.InvariantCulture);
    DeliveredSystemOutcome Outcome(string arm) => new(N(arm+"_Delivered_VSR"), N(arm+"_Delivered_CSR"), N(arm+"_Delivered_ESR"), N(arm+"_Delivered_STS"), N(arm+"_Delivered_BC"), N(arm+"_Delivered_MS"));
    CandidateDiagnostic Diagnostic(string arm) => new(false, false, N(arm+"_Diag_STS"), N(arm+"_Diag_BC"), N(arm+"_Diag_MS"));
    rows.Add(new(row["TargetId"], row["ProjectId"], int.Parse(row["RepetitionId"]), Outcome("FC"), Diagnostic("FC"), Outcome("Evo"), Diagnostic("Evo")));
}
if (rows.Count != 450 || rows.Select(x => (x.TargetId, x.RepetitionId)).Distinct().Count() != 450
    || rows.Select(x => x.ProjectId).Distinct().Count() != 58
    || rows.GroupBy(x => x.TargetId).Count() != 150
    || rows.GroupBy(x => x.TargetId).Any(g => !g.Select(x => x.RepetitionId).Order().SequenceEqual(new[] {42, 43, 44})))
    throw new InvalidDataException("Expected 450 unique pairs, 150 targets, 58 projects, repetitions 42/43/44.");
var boot = new HierarchicalBootstrap(2000, 20260919);
var metrics = new Dictionary<string, Func<DeliveredSystemOutcome, double>> {
    ["VSR"]=x=>x.VerifiedSuiteRate, ["CSR"]=x=>x.CompilationSatisfactionRate, ["ESR"]=x=>x.ExecutionSatisfactionRate,
    ["STS"]=x=>x.StructuralTargetSatisfaction, ["BC"]=x=>x.BranchCoverage, ["MS"]=x=>x.MutationScore
};
var permutation = metrics.ToDictionary(m => m.Key, m => new PairedPermutationTest(10000, 20260919).RunTest(m.Key,
    rows.GroupBy(x => x.TargetId).Select(g => g.Average(x => m.Value(x.FullChainOutcome) - m.Value(x.EvoSuiteOutcome))).ToList()));
var family = permutation.Where(x => x.Key != "VSR").OrderBy(x => x.Value.PermutationPValue).ToList();
var holm = new Dictionary<string, double>();
double previous = 0;
for (int i = 0; i < family.Count; i++) {
    previous = Math.Max(previous, Math.Min(1, family[i].Value.PermutationPValue * (family.Count - i)));
    holm[family[i].Key] = previous;
}
var output = new {
    scope = "Recalculation from rounded paired export; not verification of historical execution or configuration",
    input_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))),
    rows = rows.Count, seed = 20260919, bootstrap_resamples = 2000, permutations = 10000,
    delivered = boot.ComputeDeliveredSystemBootstrap(rows),
    candidate = boot.ComputeCandidateDiagnosticBootstrap(rows).Where(x => x.Key is "STS" or "BC" or "MS").ToDictionary(),
    permutation, holm
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllText(args[1], JsonSerializer.Serialize(output, new JsonSerializerOptions {WriteIndented=true}) + "\n");
Console.WriteLine($"Wrote {args[1]}; verified 450 paired rows and recomputed Tables 11-12.");

namespace FullChain.Contracts {
    // Minimal projections required by the linked statistical engines.
    public record DeliveredSystemOutcome(double VerifiedSuiteRate, double CompilationSatisfactionRate, double ExecutionSatisfactionRate, double StructuralTargetSatisfaction, double BranchCoverage, double MutationScore);
    public record CandidateDiagnostic(bool Compiled, bool Executed, double StructuralTargetSatisfaction, double BranchCoverage, double MutationScore);
}
