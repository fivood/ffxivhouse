# 发布脚本：full = 自用完整版（含本地直报），public = 公开版（剥离直报）
param(
    [ValidateSet("full", "public", "both")]
    [string]$Flavor = "both"
)

$ErrorActionPreference = "Stop"
$proj = "src\FF14HouseReminder.App"

function Publish-One([bool]$public, [string]$out) {
    # 两个版本的编译常量不同，必须清掉中间产物避免缓存串味
    Remove-Item "$proj\obj" -Recurse -Force -ErrorAction SilentlyContinue
    $args = @("publish", $proj, "-c", "Release", "-r", "win-x64", "--self-contained",
              "-p:PublishSingleFile=true", "-p:EnableCompressionInSingleFile=true", "-o", $out)
    if ($public) { $args += "-p:PublicBuild=true" }
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "publish $out 失败" }
    Write-Host "已发布到 $out" -ForegroundColor Green
}

if ($Flavor -eq "full" -or $Flavor -eq "both") { Publish-One $false "publish\full" }
if ($Flavor -eq "public" -or $Flavor -eq "both") { Publish-One $true "publish\public" }
