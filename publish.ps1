# 发布脚本：public = 公开版（默认构建），full = 附加功能版（要 -p:FullBuild=true 对应的源文件）
param(
    [ValidateSet("full", "public", "both")]
    [string]$Flavor = "both"
)

$ErrorActionPreference = "Stop"
$proj = "src\FF14HouseReminder.App"

function Publish-One([bool]$full, [string]$out) {
    # 两个版本的编译常量不同，必须清掉中间产物避免缓存串味
    Remove-Item "$proj\obj" -Recurse -Force -ErrorAction SilentlyContinue
    $args = @("publish", $proj, "-c", "Release", "-r", "win-x64", "--self-contained",
              "-p:PublishSingleFile=true", "-p:EnableCompressionInSingleFile=true", "-o", $out)
    if ($full) { $args += "-p:FullBuild=true" }
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "publish $out 失败" }
    Write-Host "已发布到 $out" -ForegroundColor Green
}

if ($Flavor -eq "full" -or $Flavor -eq "both") { Publish-One $true "publish\full" }
if ($Flavor -eq "public" -or $Flavor -eq "both") { Publish-One $false "publish\public" }
