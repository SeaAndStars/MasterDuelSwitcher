# 运行全部应用测试并合并真实行与分支覆盖率，指标低于100时返回失败。
param(
    # 报告目录；默认位于仓库 artifacts 内。
    [string] $OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
# 仓库根目录。
$taskRepository = Split-Path -Parent $PSScriptRoot
# 本次覆盖率的输出目录。
$taskCoverageDirectory = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskRepository 'artifacts\coverage' }
New-Item -ItemType Directory -Path $taskCoverageDirectory -Force | Out-Null
# 核心模块原始覆盖率 JSON，按相同模块和源文件合并到应用测试。
$taskCoreReport = Join-Path $taskCoverageDirectory 'core'
# 保存真实测试结果，交付证据可核对测试总数与零跳过状态。
$taskResultsDirectory = Join-Path $taskCoverageDirectory 'test-results'
dotnet test (Join-Path $taskRepository 'tests\MasterDuelSwitcher.Tests\MasterDuelSwitcher.Tests.csproj') -c Debug --logger 'trx;LogFileName=Core.trx' --results-directory $taskResultsDirectory '-p:CollectCoverage=true' ('-p:CoverletOutput=' + $taskCoreReport) '-p:CoverletOutputFormat=json' '-p:Include=[MasterDuelSwitcher.Core]*'
if ($LASTEXITCODE -ne 0) { throw 'Core 测试失败，请先修复测试。' }
# 最终合并报告同时生成机器 JSON 与通用 Cobertura XML。
$taskMergedReport = Join-Path $taskCoverageDirectory 'merged'
# App 包含真实 STA 窗口、Dispatcher 与输入边界，集合串行运行以避免共享桌面竞争；所有测试和覆盖门槛保持完整。
dotnet test (Join-Path $taskRepository 'tests\MasterDuelSwitcher.App.Tests\MasterDuelSwitcher.App.Tests.csproj') -c Debug --logger 'trx;LogFileName=App.trx' --results-directory $taskResultsDirectory '-p:CollectCoverage=true' ('-p:CoverletOutput=' + $taskMergedReport) '-p:CoverletOutputFormat=json%2ccobertura' ('-p:MergeWith=' + $taskCoreReport + '.json') '-p:Include=[MasterDuelSwitcher]*%2c[MasterDuelSwitcher.Core]*' '-p:Threshold=100' '-p:ThresholdType=line%2cbranch' '-p:ThresholdStat=total' -- xUnit.ParallelizeTestCollections=false
if ($LASTEXITCODE -ne 0) { throw '应用测试或100%行与分支覆盖率验收尚未通过，请查看报告。' }
Get-ChildItem -LiteralPath $taskCoverageDirectory | Select-Object Name,Length
