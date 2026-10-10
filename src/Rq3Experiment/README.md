# RQ3 source code

The source was imported from `HeThong2_ThucNghiem_TongHop/01_HE_THONG_2/RQ3_Evaluator_Wpf`.

- `RQ3_Evaluator_Wpf/` contains the B0/B1/B2 evaluation application and services for reading artifacts, running verifiers, and calculating metrics.
- `RQ3_Evaluator_Wpf/RQ3Evaluator.Wpf.csproj` is a Windows/WPF project targeting .NET 8.

From the repository root on Windows, build with `dotnet build src/Rq3Experiment/RQ3_Evaluator_Wpf/RQ3Evaluator.Wpf.csproj -c Release`. The batch runner is under [`../../tools/rq3/RQ3BatchRunner/`](../../tools/rq3/RQ3BatchRunner/).
