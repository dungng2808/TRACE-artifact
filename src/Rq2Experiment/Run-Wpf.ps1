$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$releaseExecutable = Join-Path $projectRoot 'src\RQ2Experiment.Wpf\bin\Release\net9.0-windows\RQ2Experiment.Wpf.exe'

if (-not (Test-Path -LiteralPath $releaseExecutable -PathType Leaf)) {
    throw "Chưa tìm thấy bản build. Hãy chạy: dotnet build RQ2Experiment.sln -c Release"
}

Start-Process -FilePath $releaseExecutable -WorkingDirectory $projectRoot
