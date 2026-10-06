# 发布 Windows x64 自包含程序，并保留中文说明及依赖许可证。
param(
    # 发布文件夹；默认放在仓库的 artifacts 目录。
    [string] $OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
# 当前仓库根目录。
$taskRepository = Split-Path -Parent $PSScriptRoot
# 明确的输出目录，避免输出到当前用户主目录。
$taskPublishPath = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskRepository 'artifacts\publish\win-x64' }
dotnet publish (Join-Path $taskRepository 'src\MasterDuelSwitcher.App\MasterDuelSwitcher.App.csproj') -c Release -r win-x64 --self-contained true -p:MasterDuelRuntimeVersion=10.0.10 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $taskPublishPath
if ($LASTEXITCODE -ne 0) { throw '发布失败，请检查构建输出。' }
Copy-Item -LiteralPath (Join-Path $taskRepository 'README.md') -Destination (Join-Path $taskPublishPath '使用说明.md')
Copy-Item -LiteralPath (Join-Path $taskRepository 'THIRD-PARTY-NOTICES.md') -Destination $taskPublishPath
Copy-Item -LiteralPath (Join-Path $taskRepository 'licenses') -Destination $taskPublishPath -Recurse -Force
Get-ChildItem -LiteralPath $taskPublishPath | Select-Object Name,Length
