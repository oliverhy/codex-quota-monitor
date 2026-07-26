$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot '..\..\outputs'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
& $csc /nologo /target:winexe /optimize+ /out:"$out\CodexQuota-Token-v5.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll CodexQuota.cs
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
