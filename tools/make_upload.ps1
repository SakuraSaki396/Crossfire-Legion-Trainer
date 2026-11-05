# =====================================================================
#  重新生成 _upload 目录：只放「该上传到 GitHub 的文件」
#
#  规则全在根目录的 .gitignore 里，这里只是把 git 认为
#  「未跟踪且未被忽略」的文件按原目录结构复制一份出来。
#
#  用法：双击同目录的 make_upload.bat，然后把 _upload 里的内容
#        全选（Ctrl+A）拖到 GitHub 的 Add file -> Upload files 页面。
# =====================================================================

$ErrorActionPreference = 'Stop'

# 仓库根目录（本脚本在 tools/ 下）
Set-Location (Split-Path $PSScriptRoot -Parent)
$root = (Get-Location).Path

if (-not (Test-Path '.git')) {
    Write-Host "[错误] 当前目录不是 git 仓库：$root" -ForegroundColor Red
    exit 1
}

Write-Host "仓库：$root"

if (Test-Path '_upload') { Remove-Item '_upload' -Recurse -Force }
New-Item -ItemType Directory '_upload' | Out-Null

# git 不一定在 PATH 里（WorkBuddy 自带的 PortableGit 就是这种情况），兜底找一下
$git = (Get-Command git -ErrorAction SilentlyContinue)
if (-not $git) {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'WorkBuddy\resources\vendor\PortableGit\mingw64\bin\git.exe'),
        (Join-Path $env:ProgramFiles 'Git\cmd\git.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Git\cmd\git.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Git\cmd\git.exe')
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { $git = Get-Item -LiteralPath $c; break }
    }
}
if (-not $git) {
    Write-Host "[错误] 找不到 git.exe，无法判断哪些文件该上传。" -ForegroundColor Red
    exit 1
}

# -o 只列未跟踪文件，--exclude-standard 应用 .gitignore
$files = @(& $git.FullName ls-files -o --exclude-standard)

if ($files.Count -eq 0) {
    Write-Host "[警告] 没有可上传的文件，可能已经全部提交过了。" -ForegroundColor Yellow
    exit 0
}

$n = 0
foreach ($f in $files) {
    $dst = Join-Path '_upload' $f
    $dir = Split-Path $dst -Parent
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    Copy-Item -LiteralPath $f -Destination $dst -Force
    $n++
}

Write-Host ""
Write-Host "完成：_upload 里共 $n 个文件。" -ForegroundColor Green
Write-Host "下一步：打开 _upload 目录，全选（Ctrl+A），拖到 GitHub 上传页面。"
Write-Host ""
