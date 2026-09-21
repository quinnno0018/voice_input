param([string]$Zig = "zig", [switch]$HandoffTrace, [switch]$LiveAsr)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$variant = if ($LiveAsr) { "live-asr" } else { "host-handoff" }
$out = Join-Path $root ("build\" + $variant + $(if ($HandoffTrace) { "-trace" } else { "" }))
New-Item -ItemType Directory -Path $out -Force | Out-Null
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $root "build\zig-cache"
$common = @("c++", "-target", "x86_64-windows-gnu", "-std=c++17", "-O1", "-g",
    "-Wall", "-Wextra", "-Werror", "-DUNICODE", "-D_UNICODE")
if ($HandoffTrace) { $common += "-DTSF_PROBE_HANDOFF_TRACE" }
[string[]]$dllFlags = if ($LiveAsr) { @("-DTSF_PROBE_LIVE_ASR") } else { @() }
& $Zig @common @dllFlags "-shared" (Join-Path $root "text_service.cpp") (Join-Path $root "registration.cpp") (Join-Path $root "exports.def") "-o" (Join-Path $out "VoiceInput.TsfProbe.dll") "-lole32" "-loleaut32" "-luuid" "-lmsctf" "-luser32" "-ladvapi32"
if ($LASTEXITCODE -ne 0) { throw "TSF DLL build failed" }
& $Zig @common (Join-Path $root "interruption_tests.cpp") "-o" (Join-Path $out "TsfProbe.InterruptionTests.exe") "-lole32" "-loleaut32" "-luuid" "-lmsctf" "-luser32"
if ($LASTEXITCODE -ne 0) { throw "TSF interruption tests build failed" }
& (Join-Path $out "TsfProbe.InterruptionTests.exe")
if ($LASTEXITCODE -ne 0) { throw "TSF interruption tests failed" }
if ($LiveAsr) {
    & $Zig @common (Join-Path $root "live_tests.cpp") "-o" (Join-Path $out "TsfProbe.LiveTests.exe") "-lole32" "-loleaut32" "-luuid" "-lmsctf" "-luser32" "-ladvapi32"
    if ($LASTEXITCODE -ne 0) { throw "TSF live tests build failed" }
    & (Join-Path $out "TsfProbe.LiveTests.exe")
    if ($LASTEXITCODE -ne 0) { throw "TSF live tests failed" }
}
& $Zig @common "-municode" (Join-Path $root "tests.cpp") "-o" (Join-Path $out "TsfProbe.Tests.exe") "-lole32" "-luuid" "-lmsctf" "-luser32"
if ($LASTEXITCODE -ne 0) { throw "TSF tests build failed" }
& (Join-Path $out "TsfProbe.Tests.exe") (Join-Path $out "VoiceInput.TsfProbe.dll")
if ($LASTEXITCODE -ne 0) { throw "TSF tests failed" }
Write-Output "Built x64 TSF probe. No input service has been registered."
