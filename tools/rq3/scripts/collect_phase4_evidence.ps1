param([string]$DatasetRoot='R:\BaoVer2\ThucNgiem\RQ3_Phase2',[string]$OutputRoot='R:\BaoVer2\ThucNgiem\RQ3_Phase2\evidence',[string]$ToolEvidenceRoot,[string]$CaseId,[string]$Project,[string]$Manifest,[string]$MavenRepoRoot='R:\BaoVer2\ThucNgiem\RQ3_Phase1\candidates',[string]$MavenDockerImage='maven:3.9.11-eclipse-temurin-17',[string]$DockerImage,[string]$JavaHome='/usr/lib/jvm/java-11-openjdk-amd64',[switch]$DryRun)
$ErrorActionPreference='Stop'
$tool=Join-Path $PSScriptRoot '..\tools\RQ3EvidenceCollector\RQ3EvidenceCollector.csproj'
$args=@('--dataset',$DatasetRoot,'--out',$OutputRoot,'--java-home',$JavaHome,'--maven-repo-root',$MavenRepoRoot,'--maven-docker-image',$MavenDockerImage);if($ToolEvidenceRoot){$args+=@('--tool-evidence-out',$ToolEvidenceRoot)};if($CaseId){$args+=@('--case',$CaseId)};if($Project){$args+=@('--project',$Project)};if($Manifest){$args+=@('--manifest',$Manifest)};if($DockerImage){$args+=@('--docker-image',$DockerImage)};if($DryRun){$args+='--dry-run'}
& dotnet run --project $tool -- $args
if($LASTEXITCODE -ne 0){throw "Evidence collector failed with exit code $LASTEXITCODE"}
