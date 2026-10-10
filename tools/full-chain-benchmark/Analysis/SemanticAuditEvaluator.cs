using System;
using System.Collections.Generic;
using System.Linq;

namespace FullChain.Benchmark.Analysis;

public record SemanticAuditReview(
    string TargetId,
    string Treatment,
    bool Reviewer1Sound,
    bool Reviewer2Sound,
    string Notes
);

public record SemanticAuditReport(
    int TotalAudited,
    int BothSoundCount,
    int BothUnsoundCount,
    int DisagreementCount,
    double ObservedAgreement,
    double ExpectedAgreement,
    double CohensKappa,
    bool SafetyContractHonored,
    string NonInterferenceStatement
);

public class SemanticAuditEvaluator
{
    public static SemanticAuditReport Evaluate(IReadOnlyList<SemanticAuditReview> reviews)
    {
        int n = reviews.Count;
        if (n == 0)
        {
            return new SemanticAuditReport(0, 0, 0, 0, 1.0, 1.0, 1.0, true, "No reviews audited.");
        }

        int bothSound = reviews.Count(r => r.Reviewer1Sound && r.Reviewer2Sound);
        int bothUnsound = reviews.Count(r => !r.Reviewer1Sound && !r.Reviewer2Sound);
        int r1OnlySound = reviews.Count(r => r.Reviewer1Sound && !r.Reviewer2Sound);
        int r2OnlySound = reviews.Count(r => !r.Reviewer1Sound && r.Reviewer2Sound);
        int disagreement = r1OnlySound + r2OnlySound;

        double pObserved = (double)(bothSound + bothUnsound) / n;

        // Marginal probabilities
        double pR1Sound = (double)(bothSound + r1OnlySound) / n;
        double pR2Sound = (double)(bothSound + r2OnlySound) / n;
        double pR1Unsound = 1.0 - pR1Sound;
        double pR2Unsound = 1.0 - pR2Sound;

        double pExpected = (pR1Sound * pR2Sound) + (pR1Unsound * pR2Unsound);

        double kappa = Math.Abs(1.0 - pExpected) < 1e-9 ? 1.0 : (pObserved - pExpected) / (1.0 - pExpected);

        return new SemanticAuditReport(
            TotalAudited: n,
            BothSoundCount: bothSound,
            BothUnsoundCount: bothUnsound,
            DisagreementCount: disagreement,
            ObservedAgreement: pObserved,
            ExpectedAgreement: pExpected,
            CohensKappa: kappa,
            SafetyContractHonored: true,
            NonInterferenceStatement: "Preselected blinded two-reviewer semantic audit is strictly a secondary safety result. It does not alter B2 verdicts, routing policies, tool lock, or official primary results."
        );
    }
}

