# RQ2 source code

The .NET source was imported from the team's package. From this directory, build and run diagnostics with:

```powershell
dotnet build RQ2Experiment.slnx --configuration Release
dotnet run --project RQ2Experiment.Runner/RQ2Experiment.Runner.csproj -- --diagnostics
```

The WPF application requires Windows. Selected result exports are under [`../../results/rq2/`](../../results/rq2/). The dataset manifest from the wider workspace is not included in this staging copy. API keys must be supplied locally and must not be committed to Git.
