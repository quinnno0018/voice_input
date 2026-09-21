param(
    [ValidateSet("Inspect", "Register", "Enable", "Unregister")][string]$Action = "Inspect",
    [switch]$ConfirmSystemChange,
    [switch]$HandoffTrace,
    [switch]$KeyboardHandoff,
    [switch]$HostHandoff,
    [switch]$LiveAsr
)
$ErrorActionPreference = "Stop"
if (-not [Environment]::Is64BitProcess) { throw "Use 64-bit PowerShell." }
if ($HostHandoff -and $KeyboardHandoff) { throw "Choose either HostHandoff or KeyboardHandoff, not both." }
if ($LiveAsr -and ($HostHandoff -or $KeyboardHandoff)) { throw "LiveAsr cannot be combined with another build variant." }
$directory = if ($LiveAsr) {
    if ($HandoffTrace) { "build\live-asr-trace" } else { "build\live-asr" }
} elseif ($HostHandoff) {
    if ($HandoffTrace) { "build\host-handoff-trace" } else { "build\host-handoff" }
} elseif ($KeyboardHandoff) {
    if ($HandoffTrace) { "build\keyboard-handoff-trace" } else { "build\keyboard-handoff" }
} else {
    if ($HandoffTrace) { "build\handoff-trace" } else { "build\input-guard" }
}
$dll = Join-Path $PSScriptRoot "$directory\VoiceInput.TsfProbe.dll"
$clsid = "{876623A8-16EF-4834-A525-7AB17EA09C83}"
$profile = "{DD7E2E9D-44F2-4D12-A95D-1728238C206F}"
$inputTip = "0804:$clsid$profile"
$comPath = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID\$clsid"
$tipPath = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\CTF\TIP\$clsid"
if ($Action -eq "Inspect") {
    $server = Get-Item -LiteralPath "$comPath\InprocServer32" -ErrorAction SilentlyContinue
    [pscustomobject]@{
        Dll = $dll
        RegisteredDll = if ($server) { $server.GetValue("") } else { $null }
        Built = Test-Path -LiteralPath $dll
        ComRegistered = Test-Path -LiteralPath $comPath
        TipRegistered = Test-Path -LiteralPath $tipPath
        UserListed = [bool](@(Get-WinUserLanguageList | ForEach-Object { $_.InputMethodTips }) -contains $inputTip)
        InputProfile = "Voice Input TSF Probe (Chinese Simplified)"
        Trigger = "Ctrl+Alt+J"
    }
    return
}
if (-not $ConfirmSystemChange) {
    throw "System registration needs explicit approval. After approval, pass -ConfirmSystemChange."
}
function Set-ProbeUserProfile([bool]$Enabled) {
    if (-not ("VoiceInput.TsfProfileSetup" -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
namespace VoiceInput {
    public static class TsfProfileSetup {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("input.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InstallLayoutOrTip(string profile, uint flags);
    }
}
'@
    }
    # Only this profile is affected. Never use DEFPROFILE or CLEANINSTALL.
    $flags = if ($Enabled) { [uint32]0 } else { [uint32]1 }
    if (-not [VoiceInput.TsfProfileSetup]::InstallLayoutOrTip("0x0804:$clsid$profile", $flags)) {
        throw "InstallLayoutOrTip failed. Inspect before retrying; no success is assumed."
    }
}
if ($Action -eq "Enable") {
    if (-not (Test-Path -LiteralPath $comPath) -or -not (Test-Path -LiteralPath $tipPath)) {
        throw "Register the COM/TSF service before enabling its user profile."
    }
    Set-ProbeUserProfile $true
    Write-Output "Experiment added to the current user's input methods; no default-setting flag used."
    return
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Registration requires an elevated 64-bit PowerShell. No changes were made."
}
if (-not (Test-Path -LiteralPath $dll)) { throw "Build the DLL first." }
if ($Action -eq "Unregister") { Set-ProbeUserProfile $false }
$regsvr = Join-Path $env:WINDIR "System32\regsvr32.exe"
$quotedDll = '"' + $dll + '"'
$arguments = if ($Action -eq "Register") { @("/s", $quotedDll) } else { @("/s", "/u", $quotedDll) }
$process = Start-Process -FilePath $regsvr -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw "regsvr32 failed: exit $($process.ExitCode). Inspect the registration before retrying." }
if ($Action -eq "Register") { Set-ProbeUserProfile $true }
Write-Output "$Action completed for $clsid. No default input source was changed."
