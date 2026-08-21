# Builds BitChroma.exe using the in-box .NET Framework compiler (no SDK required).
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "csc.exe not found - .NET Framework 4.x is required." }
$out = Join-Path $here "BitChroma.exe"
& $csc /nologo /target:winexe /optimize+ /platform:anycpu /out:$out `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    (Join-Path $here "BitChroma.cs")
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
Write-Host ("Built {0} ({1:N0} bytes)" -f $out, (Get-Item $out).Length)
