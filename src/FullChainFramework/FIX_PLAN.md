# Kế hoạch sửa lỗi FullChainFramework

## 1. Mục tiêu và phạm vi

Khôi phục một đường chạy FullChain có thể kiểm chứng được từ cả WPF và CLI, với cùng một hành vi đối với tạo test, sửa test, đánh giá, xác minh và resume. Kế hoạch này sửa các lỗi đã phát hiện trong vòng đời campaign; không thay đổi giao thức nghiên cứu (150 target, repetition `42/43/44`, tối đa 2 vòng repair, tối đa 3 lượt verification).

Các lỗi cần xử lý:

1. CLI `run`, `resume`, `export` mới là stub.
2. RQ3 không nhận được focal class/method thật, đồng thời nhận cứng pass/budget là `1`.
3. Checkpoint lưu trước transition; resume có thể bỏ qua repair và gọi lại B2 không cần thiết.
4. RQ2 xử lý API key khác RQ1/RQ3 và làm thay đổi `PATH`/`JAVA_HOME` của cả process.
5. Test harness mặc định có thể gọi LLM thật và thay đổi thư mục evidence/workspace, nên không phù hợp cho kiểm thử hồi quy thông thường.

## 2. Nguyên tắc triển khai

- Không ghi API key vào JSON, log, checkpoint, artifact hay test fixture.
- Không sửa source checkout của dataset. Mọi Java test, POM instrumentation và evidence chỉ ở workspace cô lập trong `experiments/full-chain/runs/`.
- Checkpoint phải biểu diễn **next executable state**, không phải stage vừa bắt đầu.
- Chỉ `LOCAL_TEST_REPAIR` được quay lại RQ2; mọi route khác kết thúc có lý do rõ ràng.
- Tất cả test mặc định là offline/deterministic. Live LLM/Maven campaign phải là lệnh tường minh.
- Hoàn thành một phase chỉ khi build, test regression của phase đó và acceptance criteria đều pass.

## 3. Lộ trình theo phase

### Phase 0 — Baseline an toàn và contract hiện trạng

**Mục tiêu:** Có baseline tái lập được trước khi đổi behavior.

**Công việc**

- Ghi lại output của `validate-config`, `doctor`, version .NET/Java/Maven và hash manifest/tool-lock vào thư mục run riêng, không thay đổi config/dataset.
- Liệt kê các caller của `IVerifierAdapter`, `CommonEnvelope`, checkpoint và RQ2 credential để bảo đảm sửa contract không bỏ sót EvoSuite adapter hoặc benchmark tool.
- Tách rõ test `offline` và `live` trong `FullChain.Tests/Program.cs`; trong phase này chỉ thay đổi entry selection, chưa đổi test logic.
- Kiểm tra solution build cho `net8.0`; WPF `net8.0-windows` chỉ build trên môi trường Windows hoặc với Windows targeting phù hợp.

**Tiêu chí hoàn thành**

- Có một lệnh offline không gọi mạng/LLM và không ghi vào dataset checkout.
- Có bản snapshot cấu hình đã redacted để so sánh trước/sau.

---

### Phase 1 — Chuẩn hoá dữ liệu xác minh RQ3

**Mục tiêu:** RQ3 luôn nhận đúng target, test identity và trạng thái vòng lặp hiện tại.

**File chính**

- `FullChain.Contracts/Artifacts.cs`
- `FullChain.Contracts/Interfaces.cs`
- `FullChain.Infrastructure/Rq3/VerificationEnvelopeFactory.cs`
- `FullChain.Infrastructure/Rq3/Rq3VerifierAdapter.cs`
- `FullChain.Core/Orchestration/FullChainOrchestrator.cs`
- `tools/full-chain-benchmark/EvoSuiteAdapter.cs`

**Công việc**

1. Thêm một immutable `VerificationRequest` vào Contracts, chứa:
   - `TargetDescriptor Target`
   - `CommonEnvelope Evaluation`
   - test class FQN và relative artifact path thật
   - `CurrentVerificationPass`, `RemainingRepairRounds`
   - `ScenarioToMethodMap` nếu cần cho conformance audit.
2. Đổi `IVerifierAdapter.VerifyBlindedEnvelopeAsync` để nhận `VerificationRequest` thay vì chỉ `CommonEnvelope`.
3. Xoá overload suy diễn `TargetDescriptor` từ `CommonEnvelope`, vì `CommonEnvelope` hiện không có focal class/method/source path. `VerificationEnvelopeFactory` chỉ materialize từ request có target thật.
4. Đổi `Rq3VerifierAdapter` phụ thuộc vào `ILlmClient`, không phụ thuộc concrete `OpenAiCompatibleClient`, để test được bằng fake client.
5. Truyền `state.VerificationPass` và `MaxRepairRounds - state.RepairRound` vào RQ3 thay vì constants `1, 1`.
6. Chuẩn hoá constants cho verdict và route; thay comment `LOCAL_REPAIR` cũ trong Contracts bằng `LOCAL_TEST_REPAIR`.
7. Validate output specialist: status/cause ngoài whitelist phải thành `ABSTAIN` kèm lý do, không được tự coi là `CLEAR`.

**Test bắt buộc**

- Envelope chứa focal class, method signature, revision và artifact path từ target thật.
- Lượt verification 1/2/3 và số repair còn lại được adapter nhận chính xác.
- Ba case: all-clear → `VERIFIED`; local failure → `REPAIR_REQUIRED/LOCAL_TEST_REPAIR`; malformed/provider error → `ABSTAIN`.

**Tiêu chí hoàn thành**

- Không còn code nào tạo `FocalClass = envelope.TargetId` hoặc `FocalMethodSignature = ""` cho RQ3.
- `VerificationRoutePolicy` và state machine cùng dùng một vocabulary route/verdict.

---

### Phase 2 — Sửa checkpoint và resume theo state thật

**Mục tiêu:** Resume không bỏ repair, không gọi lại LLM cho một B2 đã hoàn tất, và không dùng workspace cũ bẩn.

**File chính**

- `FullChain.Core/Orchestration/FullChainOrchestrator.cs`
- `FullChain.Infrastructure/Persistence/CheckpointStore.cs`
- `FullChain.Contracts/RunModels.cs` và/hoặc `Artifacts.cs`

**Công việc**

1. Lưu checkpoint **sau khi stage hoàn thành và state machine đã transition**. `CurrentStage` trong payload phải là stage sẽ chạy tiếp theo.
2. Thêm `FailureEvidence? LatestFailure` vào `OrchestratorCheckpointPayload`.
3. Bỏ `AdvanceFromCompletedStage`, hoặc thay bằng logic tương thích chỉ dùng cho checkpoint schema cũ. Không được tự động map `CANDIDATE_COMPILE_AND_EXECUTE → COMMON_EVALUATION`.
4. Khi resume vào `REPAIR`, sử dụng failure/scenario mapping đã persist; khi resume vào candidate/evaluation, materialize lại test source vào workspace mới.
5. Khi B2 đã trả decision, persist state sau decision; resume phải áp dụng state đó hoặc trả terminal record, không gọi verifier lần hai.
6. Gắn `schemaVersion` cho checkpoint payload và viết migration/read fallback cho checkpoint cũ; checkpoint không tương thích phải báo rõ thay vì silently chuyển stage sai.
7. Giữ atomic write bằng temporary file + move; tránh `Delete` trước `Move` nếu có API replace an toàn trên platform hiện tại.

**Test bắt buộc**

- Dừng sau generation → resume chạy candidate đúng một lần.
- Dừng sau candidate failure có mapping → resume vào repair, không nhảy evaluation.
- Dừng sau repair → resume candidate với repaired source.
- Dừng sau B2 → resume không tạo thêm request LLM/verifier.
- Input hash thay đổi → resume bị từ chối.

**Tiêu chí hoàn thành**

- Mọi checkpoint tái lập đúng next stage và toàn bộ dữ liệu cần thiết cho stage đó.
- Không có test nào quan sát repair bị skip hoặc B2 bị gọi hai lần vì resume.

---

### Phase 3 — Credential và process environment nhất quán

**Mục tiêu:** RQ1, RQ2, RQ3 dùng cùng một cơ chế resolve key và không gây side effect toàn process.

**File chính**

- `FullChain.Infrastructure/LLM/OpenAiCompatibleClient.cs`
- `FullChain.Infrastructure/Rq2/Rq2RepairAdapter.cs`
- `FullChain.Wpf/MainWindowViewModel.cs`
- tạo `FullChain.Infrastructure/LLM/ApiKeyResolver.cs`

**Công việc**

1. Tạo `IApiKeyResolver` với thứ tự rõ ràng: key đã cung cấp in-memory → environment variable theo config → file path đã resolve từ repo root. Giá trị key chỉ là memory value.
2. WPF và CLI resolve key một lần ở composition root rồi inject vào RQ1/RQ2/RQ3.
3. RQ2 không tự đọc riêng `ApiKeyFilePath`; fail sớm bằng lỗi redacted nếu provider cần key nhưng resolver không có giá trị.
4. RQ2 không cố định `ProviderName = "OpenRouter"` khi config dùng provider khác; validate provider/protocol tương thích hoặc support adapter tương ứng.
5. Thay global `Environment.SetEnvironmentVariable` bằng environment injection nếu RQ2 dependency hỗ trợ. Nếu dependency chưa hỗ trợ, lưu `PATH`/`JAVA_HOME` cũ và phục hồi trong `finally`, đồng thời cấm parallel repair trong process.
6. Đảm bảo mọi exception, evidence và UI log redacted key.

**Test bắt buộc**

- Resolver ưu tiên đúng và không đưa key vào `ToString`, JSON hay log.
- RQ2 dùng environment key được khi file key không tồn tại.
- `PATH` và `JAVA_HOME` không đổi sau repair success/failure/cancel.

**Tiêu chí hoàn thành**

- Không còn hai đường credential có behavior khác nhau giữa RQ1/RQ2/RQ3.

---

### Phase 4 — Tạo composition layer dùng chung và hoàn thiện CLI

**Mục tiêu:** `run`, `resume`, `export` là lệnh thật; WPF và CLI không còn dựng dependency theo hai cách khác nhau.

**Thiết kế đề xuất**

Tạo project library mới `FullChain.Runner` trong `src/FullChainFramework/`. Project này là composition layer duy nhất, reference Contracts/Core/Infrastructure và `tools/full-chain-benchmark`; nó cung cấp `CampaignRunnerService`, `CampaignRequest`, `CampaignProgress` và factory dependency. Cả CLI lẫn WPF đều reference layer này.

**Công việc**

1. Di chuyển phần dựng `ToolchainResolver`, LLM client, RQ1/RQ2/RQ3, orchestrator, EvoSuite và `PairedCampaignRunner` từ WPF vào `FullChain.Runner`.
2. WPF chỉ chuyển UI setting thành `CampaignRequest`, subscribe progress/log và render result.
3. Implement CLI:
   - `run --config <path> --manifest <path> --target <id>[,...] --repetition <n>[,...] --campaign-id <id>`
   - `resume` dùng cùng options, yêu cầu checkpoint phù hợp input hash.
   - `export --runs <path> --out <path>` gọi exporter/analysis hiện có trong benchmark tool; nếu exporter thiếu API public, tạo API đó thay vì giả lập thành công.
4. Hỗ trợ Ctrl+C bằng `CancellationToken`; return non-zero cho validation/tool/provider failure.
5. `doctor` kiểm tra binary version/hash theo tool-lock, không chỉ kiểm tra file tồn tại. `full-chain-preflight` dùng cùng config resolver thay vì hard-code path platform.
6. In machine-readable run summary JSON khi hoàn tất để CI có thể kiểm tra terminal status.

**Test bắt buộc**

- CLI `run` với fake runtime đi qua đầy đủ state machine và tạo record.
- CLI `resume` gọi cùng service với WPF, không tạo implementation thứ hai.
- `export` tạo file kết quả thật hoặc trả lỗi rõ ràng; không còn message "engine ready".

**Tiêu chí hoàn thành**

- Không còn `RunExecutionAsync`, `RunResumeAsync`, `RunExportAsync` dạng stub.
- Một `CampaignRequest` có cùng kết quả khi khởi động từ CLI hoặc WPF (với fake dependencies giống nhau).

---

### Phase 5 — Regression suite offline và live gate

**Mục tiêu:** Có bằng chứng tự động cho các lỗi đã sửa mà không phát sinh chi phí API ngoài ý muốn.

**File chính**

- `FullChain.Tests/Program.cs` (hoặc tách thành unit/integration project nếu phù hợp)
- Các fake test double cho `ILlmClient`, `IProcessRunner`, `ICheckpointStore` và workspace fixture.

**Công việc**

1. Mặc định `FullChain.Tests` chỉ chạy offline deterministic tests.
2. Chuyển live phase hiện có sang `--live --phase <n>`; lệnh phải kiểm tra key/toolchain trước khi gọi provider.
3. Không để lệnh không có argument tự động chạy Phase 3, 5, 6, 10, 11 vì các phase này có thể gọi provider, Maven/PIT/EvoSuite hoặc tạo evidence.
4. Thêm coverage cho Phase 1–4 của kế hoạch này, đặc biệt resume matrix và verifier envelope.
5. Thêm test schema compatibility cho checkpoint v1/v2 và config validation.
6. Đặt toàn bộ test workspace dưới temporary directory được tạo cho test; cleanup bằng `try/finally`; tuyệt đối không dùng dataset source checkout làm output.

**Tiêu chí hoàn thành**

- Offline suite không cần API key/network và chạy lặp lại cùng kết quả.
- Live suite chỉ chạy sau action tường minh của operator.

---

### Phase 6 — Xác minh end-to-end và rollout có kiểm soát

**Mục tiêu:** Chứng minh đường chạy mới hoạt động trước khi chạy official campaign.

**Thứ tự chạy**

1. Build solution và chạy offline regression suite.
2. `doctor`, `validate-config`, `full-chain-preflight` với official config.
3. Chạy CLI một target, một repetition trong campaign ID mới, ví dụ `repair-validation-<timestamp>`.
4. Chủ động cancel ở từng mốc generation, candidate failure/repair, evaluation và B2 để xác minh resume matrix.
5. Chạy WPF cùng một target bằng campaign ID khác, so sánh record schema, terminal status và artifact index với CLI.
6. Chạy pilot manifest 15 target; chỉ khi readiness gate pass mới xin phép chạy official 150 × 3.

**Acceptance criteria cuối**

- CLI/WPF cùng dùng một runtime path và không có stub.
- Envelope RQ3 có full target identity; pass/budget thật được audit trong artifact.
- Resume không bỏ repair hoặc duplicate B2.
- Không lộ secret và process environment được phục hồi.
- Offline regression pass, pilot completion/evidence/readiness pass.

## 4. Thứ tự ưu tiên và dependency

| Ưu tiên | Phase | Lý do |
| --- | --- | --- |
| P0 | 0 | Tạo baseline/test an toàn trước khi đổi code. |
| P1 | 1 | RQ3 hiện thiếu dữ liệu xác minh; đây là lỗi correctness cốt lõi. |
| P1 | 2 | Resume sai có thể làm sai số liệu campaign. |
| P1 | 3 | Credential/environment có thể làm pipeline fail không nhất quán. |
| P2 | 4 | Đưa CLI từ stub thành entry point thật sau khi contracts đã ổn định. |
| P2 | 5 | Khoá regression để tránh tái phát. |
| P3 | 6 | Live/pilot chỉ chạy khi các phase trước hoàn tất. |

## 5. Điều kiện cần phê duyệt trước khi chạy Phase 6

Phase 6 có thể gọi OpenRouter, tải Maven dependencies và ghi evidence/runs. Cần xác nhận trước khi chạy live rằng chi phí API, campaign ID, target/repetition và thư mục output là đúng ý người vận hành.

## 6. Nhật ký triển khai

Trạng thái cập nhật ngày 2026-09-20. Các thay đổi dưới đây chỉ áp dụng cho `src/FullChainFramework/` và `tools/full-chain-benchmark/`; source checkout dataset không bị chỉnh sửa.

| Phase | Trạng thái | Bằng chứng |
| --- | --- | --- |
| 0 | Hoàn thành | Official config được validate; Java, Maven và EvoSuite hash được doctor xác nhận; snapshot redacted nằm ở `experiments/full-chain/runs/phase0-baseline-20260920/BASELINE.md`. Credential readiness là gate riêng và hiện fail-redacted như mong đợi. |
| 1 | Hoàn thành | `VerificationRequest` mang target/FQN/path/pass/budget thật; envelope audit cả verification pass và repair budget; RQ3 dùng vocabulary verdict/route chung và malformed specialist output dẫn tới `ABSTAIN`. |
| 2 | Hoàn thành | Checkpoint schema v2 lưu next state sau transition, gồm failure evidence; atomic replace; regression bao phủ resume sau generation, candidate failure/repair, repair hoàn tất, B2/delivered, legacy schema và input-hash mismatch. |
| 3 | Hoàn thành | Một `ApiKeyResolver` dùng chung; RQ2 dùng `ScopedToolchainEnvironment` có lock và hồi phục `PATH`/`JAVA_HOME` ở normal/failure/cancel; credential OpenRouter sai định dạng bị chặn mà không in secret. |
| 4 | Hoàn thành | `FullChain.Runner` là composition root chung của WPF/CLI; `run`, `resume`, `export`, `doctor`, `preflight` là lệnh thật; `doctor` kiểm tra tool-lock/hash và credential redacted; raw record writer/exporter tương thích cả JSONL mới và file pretty-printed cũ; credential gate trả non-zero trước khi tạo workspace/checkpoint. |
| 5 | Hoàn thành | Default test chạy offline (10/10 pass). Bộ regression bao phủ secret/config, key precedence, RQ3 target contract/bounded loop, full checkpoint resume matrix, legacy/hash rejection, environment restoration và credential gate không tạo output; live test đòi hỏi `--live --phase`. |
| 6 | Pilot CLI hoàn thành; B2 remediation pass; chưa đủ điều kiện chạy official | Với endpoint cục bộ và credential đã redacted, build sạch (0 warning/error), offline regression 11/11 pass và `doctor` local pass. Campaign cũ `phase6-local-pilot15-20260920-r42` đã chạy 15 target × rep 42, ghi 30 raw records/15 paired rows, không thiếu evidence và readiness ledger eligible. Export độc lập tái tạo raw JSONL byte-for-byte; `resume` skip đúng 15 cặp đã hoàn tất, không gọi lại provider. Một `BudgetExhausted` là terminal outcome hợp lệ nên CLI không biến campaign hoàn chỉnh thành exit 1. Audit pilot cũ tìm được B2 thiếu plan scope/PIT XML, JaCoCo gộp sibling source file và STS đo return/exception như branch. Đã sửa focal evidence/B2 prompt; campaign kiểm chứng `phase6-b2-remediation-20260920-pt-o-001-r42` cho FullChain và EvoSuite đều `VerifiedByB2`, có PIT XML thật và checkpoint audit xác nhận chỉ target do plan claim được xét. Pilot mới `phase6-b2-pilot15-20260920-r42` chạy đủ 15/15 cặp (30 records), `missingEvidenceCount=0`, raw export trùng byte-for-byte và ledger eligible. Tuy nhiên chỉ PT-O-001 được B2 xác minh cho mỗi treatment (FullChain 1/15; EvoSuite 1/15); FullChain còn 9 `RepairExhausted`, 3 `BudgetExhausted`, 1 `VerifierRejected`, 1 `Unverified`, trong khi EvoSuite có 9 `RepairExhausted`, 5 `GenerationFailed`. Đây là kết quả chất lượng thực tế, chưa chứng minh pipeline đạt tỷ lệ verified đủ tốt để scale 150 × 3. `full-chain.official.json` vẫn trỏ OpenRouter và credential bị reject-redacted; WPF UI live cũng chưa kiểm chứng được trên host macOS. Không chạy official 150 × 3 nếu chưa cải thiện RQ1/TGSLR/budget, chạy lại pilot đạt tiêu chí verified rõ ràng, có config provider chính thức hợp lệ và xác nhận scope/cost. |

**Cập nhật sau remediation RQ1/RQ2/PIT (2026-09-20):** Lỗi placeholder làm generator không nhận finalized plan đã được sửa; RQ2 nhận plan/scenario/method B2 cụ thể; PIT dùng FQN test thay vì simple name (trước đó chạy 0 test package-scoped và báo `NO_COVERAGE` giả). Build sạch và offline regression 14/14 pass. Kiểm chứng đơn `phase6-pit-fqn-pt-o-009-20260920-r42`: FullChain/EvoSuite đều `VerifiedByB2`, mutation score 100 và PIT chạy test thật. Pilot hậu sửa `phase6-post-rq1-rq2-pit-pilot15-20260920-r42` đủ 15/15 cặp, 30 record, `missingEvidenceCount=0`, export raw JSONL byte-for-byte và ledger eligible; FullChain `VerifiedByB2` 10/15, EvoSuite 6/15. Năm FullChain chưa hoàn thành là PT-O-036 (route B2 không repairable), PT-O-051/PT-O-060/PT-O-066 (global token cap sau repair), PT-O-074 (repair exhausted). Giữ nguyên quyết định chưa chạy official: cần xử lý nhóm failure còn lại, cấu hình provider official hợp lệ, và xác nhận scope/cost.

### Lệnh xác minh đã chạy

```text
dotnet restore src/FullChainFramework/FullChainFramework.sln
dotnet build src/FullChainFramework/FullChainFramework.sln --no-restore
dotnet run --project src/FullChainFramework/FullChain.Tests -- --offline
dotnet run --project src/FullChainFramework/FullChain.Cli -- validate-config experiments/full-chain/configs/full-chain.official.json
dotnet run --project src/FullChainFramework/FullChain.Cli -- doctor --config experiments/full-chain/configs/full-chain.official.json
dotnet run --project src/FullChainFramework/FullChain.Cli -- full-chain-preflight --config experiments/full-chain/configs/full-chain.official.json --target PT-O-001
dotnet run --project src/FullChainFramework/FullChain.Cli -- export --runs experiments/full-chain/runs/phase6-pilot-20260920-pt-o-001-r42 --out experiments/full-chain/runs/phase6-pilot-20260920-pt-o-001-r42/export-validation
```

`export-validation/` đã có `raw-records.jsonl`, paired CSV, availability ledger và các report pilot. Đây chỉ là artifact kiểm thử export, không phải kết quả nghiên cứu hợp lệ: record FullChain trong pilot bị provider failure và record EvoSuite là unverified.
