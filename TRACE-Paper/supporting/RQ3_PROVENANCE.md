# RQ3 external-artifact provenance

This manifest records the external workspace artifacts used to check the RQ3
metric definitions and the numerical consistency of the manuscript. The files
below are **not included in this Git repository**. Their presence and hashes on
one local workspace do not constitute a public empirical release.

## Frozen dataset and scoring implementation

| Artifact | Workspace path (relative to `R:\BaoVer2`) | SHA-256 |
|---|---|---|
| Exploratory-v2 cohort manifest | `ThucNgiem/RQ3_Phase3_Consensus/phase6_exploratory_v2_dataset/registry/phase6_exploratory_v2_manifest.json` | `8A29C26A701007C3008A291EC6C3240081B4D3DD56E96A07468718B90F154669` |
| Exploratory-v2 scoring script | `HeThong2_ThucNghiem_TongHop/01_HE_THONG_2/scripts/score_exploratory_v2.ps1` | `41E824D5BA178BBA9C6F4D31F78BB2ADDFCBE98DCF5E4D289118C955EE4333A0` |
| B2 deterministic aggregator | `HeThong2_ThucNghiem_TongHop/01_HE_THONG_2/RQ3_Evaluator_Wpf/B2Aggregator.cs` | `E8991CB5C3D0097249BB0A38965E16DF144FD254F5B09D4AD967EA10B57D07D3` |
| RQ3 protocol v2 | `HeThong2_ThucNghiem_TongHop/01_HE_THONG_2/protocol/RQ3_PROTOCOL_V2.md` | `6A64E36E1833EDE746EF0FDEFB0EE84579B34A7FA0F2DAD3E34BC9B0995B9484` |

The 73 included public cases each identify a developer-written test, repository
URL, pinned revision, and source hash. Eight cases are natural and retain the
developer test. The other 65 are controlled challenges intentionally created
from those tests; the intervention may change test source, the stated scenario,
or the evidence made available to a verifier. The controlled-injection library
is at `ThucNgiem/RQ3_Phase2/protocol/controlled_injection_library_v1.md` and
the frozen public cases and private gold are under the dataset directory above.
Repository origin alone does not establish the semantic correctness of every
test or label.

## Joined prediction/gold records and metric reports

| Model/run | Artifact | SHA-256 |
|---|---|---|
| Gemini 3.5 exploratory v2, `20260826_101118` | `HeThong2_ThucNghiem_TongHop/03_KET_QUA/01_DUNG_TRONG_BAO/KQ_Thucnghiem_VerHeThong2_gemini_3_5_flash_google_ai_studio_exploratory_v2_20260826_101118/results/predictions_joined_private_gold_v2.csv` | `783AEDE26A8F83679CAD1A4A287429EE0507339DEB7E63513D3676CEED5627BF` |
| Gemini 3.5 exploratory v2, `20260826_101118` | `HeThong2_ThucNghiem_TongHop/03_KET_QUA/01_DUNG_TRONG_BAO/KQ_Thucnghiem_VerHeThong2_gemini_3_5_flash_google_ai_studio_exploratory_v2_20260826_101118/results/metrics_v2.json` | `63B965134CDE417CB975B48D478BE0E79EEA4C6666927EB9348D47D2D265C3B2` |
| DeepSeek V4 repaired sensitivity, `20260831` | `HeThong2_ThucNghiem_TongHop/03_KET_QUA/01_DUNG_TRONG_BAO/KQ_Thucnghiem_VerHeThong2_deepseek_v4_pro_exploratory_v2_free_repaired_20260831/predictions_joined_private_gold_v2.csv` | `6451F7C11D63A7AE774D29CDE7CF10DBC9CFBC8FDAEDC731C1210D2DFDFC2832` |
| DeepSeek V4 repaired sensitivity, `20260831` | `HeThong2_ThucNghiem_TongHop/03_KET_QUA/01_DUNG_TRONG_BAO/KQ_Thucnghiem_VerHeThong2_deepseek_v4_pro_exploratory_v2_free_repaired_20260831/metrics_v2.json` | `3D240F687C5BDA0C27BC3B3B8F77BF7DD2335116036AFBB71DF62D1C1BA6B860` |
| Qwen3.8 Flash sensitivity, `20260904` | `HeThong2_ThucNghiem_TongHop/03_KET_QUA/02_HOP_LE_KHONG_DUNG_TRONG_BAO/KQ_Thucnghiem_VerHeThong2_qwen3_8_flash_bai_free_20260904/results/predictions_joined_private_gold_v2.csv` | `DAD17E38051E69CF7A9DA75A207564DC050E40E55FD1084EAF9713801CA8F105` |
| Qwen3.8 Flash sensitivity, `20260904` | `HeThong2_ThucNghiem_TongHop/03_KET_QUA/02_HOP_LE_KHONG_DUNG_TRONG_BAO/KQ_Thucnghiem_VerHeThong2_qwen3_8_flash_bai_free_20260904/results/metrics_v2.json` | `61DA8A5803B027F3A1B56D33169D9E281B55CA0DC0201EEF7AF423482C030C0C` |
| Qwen3.8 Flash frozen predictions | `HeThong2_ThucNghiem_TongHop/03_KET_QUA/02_HOP_LE_KHONG_DUNG_TRONG_BAO/KQ_Thucnghiem_VerHeThong2_qwen3_8_flash_bai_free_20260904/frozen/predictions_merged_v2.json` | `903EA5372283281A89B01A501A45DC17ABFC1AF512BAC14D4480C20D694168A5` |

The Qwen source remains under the local package's historical
`02_HOP_LE_KHONG_DUNG_TRONG_BAO` directory although this manuscript
revision now uses it. The directory name is not a quality judgment. Its
manifest and metrics report the same frozen cohort hash, 73 evaluation cases,
219 prediction rows and zero case-level output errors. Qwen B2 has 219/219
valid specialist findings. According to the author, one team member labeled
all 73 included cases and the team reviewed and settled the final gold labels
through oral discussion. No case-level record of individual judgments or
adjudication was kept. Human agreement and label validity therefore could not
be independently checked from these artifacts.

## Model selection and excluded run

Qwen was selected **after** inspecting four complete System 2 alternatives on
the same 73-case cohort. The B2-minus-B1 false-pass F1 differences were
+2.08 percentage points for DeepSeek V4 Flash, +8.27 for Gemini 3.7 Google,
+10.01 for Gemini 3.7 local medium, and +17.46 for Qwen3.8 Flash. On Qwen,
attribution Macro-F1 instead fell 45.49% to 21.84% and routing remained 59.26%.
This is an outcome-informed exploratory sensitivity comparison, not a
prespecified confirmatory model selection.

The four reports are all in `HeThong2_ThucNghiem_TongHop/03_KET_QUA/02_HOP_LE_KHONG_DUNG_TRONG_BAO/`;
the names below identify their run directories and the hashes identify each
`results/metrics_v2.json`:

| Run directory suffix | Metrics SHA-256 | B2 minus B1 F1 | B2 minus B1 attribution | B2 minus B1 routing |
|---|---|---:|---:|---:|
| `deepseek_v4_flash_bai_free_20260831` | `F6056280DC3FCF976BF92CE37227C8F516D0685D11A2AC2C9E9D0C53FC65A753` | +2.08 pp | -3.28 pp | 0.00 pp |
| `gemini_3_7_flash_google_ai_studio_exploratory_v2_20260826_185008` | `CA8ACB849A054DACBF5FE8109F9FD93B8493C6906409546CC2CDBCAD8553C32F` | +8.27 pp | -6.67 pp | 0.00 pp |
| `gemini_3_7_flash_medium_local_api_exploratory_v2_20260829_232624` | `10476370A576F9B88EB60283DB2FAED08E7CD9ED38DC64A5F5B26F0A86A97315` | +10.01 pp | +2.78 pp | -5.56 pp |
| `qwen3_8_flash_bai_free_20260904` | `61DA8A5803B027F3A1B56D33169D9E281B55CA0DC0201EEF7AF423482C030C0C` | +17.46 pp | -23.65 pp | 0.00 pp |

The earlier System 2 GLM report is excluded from the manuscript's numerical
comparison because 37 B1 case-level outputs were invalid and 50 B2 outputs
were partially invalid. All 219 prediction rows are present, but 87 outputs
are not fully valid. The earlier GLM numerical provenance remains available in
Git history and the local System 2 package; it must not be interpreted as
confirmatory evidence.

## Checks supported by these artifacts

- The frozen cohort contains 73 cases per verifier: 54 gold
  `REPAIR_REQUIRED`, 8 gold `VERIFIED`, and 11 gold `ABSTAIN` cases.
- Attribution Macro-F1 is computed on the 54 repair-required cases over the
  seven gold defect causes, with zero returned for a zero denominator.
- Route correctness uses membership in each case's `acceptableRoutes` list.
- B2 prioritizes valid detected causes, requires three valid `CLEAR` findings
  for `VERIFIED`, and otherwise safely abstains when no valid defect finding is
  available.
- All displayed runs retain all 73 cases in their denominators. Qwen has no
  invalid case-level outputs; the excluded GLM run is documented above.

This record does not verify the RQ1, RQ2, or TRACE--EvoSuite raw data and
does not replace a versioned, redacted, independently accessible empirical
package.
