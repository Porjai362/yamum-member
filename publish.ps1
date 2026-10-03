# Build + sign a release for auto update.
#   1. Bump [assembly: AssemblyVersion("x.y.z")] in server.cs
#   2. powershell -ExecutionPolicy Bypass -File publish.ps1 -Notes "what changed"
#   3. Upload dist\YaMumMember.exe and dist\update.json to the update location
#      (or pass -Target <folder> to copy them to a shared folder)
# keys\update-private.xml signs releases. Keep it secret and backed up:
# without it you cannot publish updates that existing installs will accept.
param(
  [string]$Notes = "",
  [string]$Target = ""
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$src = [IO.File]::ReadAllText("$PSScriptRoot\server.cs")
$m = [regex]::Match($src, 'AssemblyVersion\("(\d+\.\d+\.\d+)"\)')
if (-not $m.Success) { throw 'AssemblyVersion("x.y.z") not found in server.cs' }
$version = $m.Groups[1].Value

$dist = "$PSScriptRoot\dist"
New-Item -ItemType Directory -Force $dist | Out-Null
# build straight into dist so publishing works even while YaMumMember.exe is running
cmd /c "`"$PSScriptRoot\build.bat`" `"$dist\YaMumMember.exe`""
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$keyFile = "$PSScriptRoot\keys\update-private.xml"
if (-not (Test-Path $keyFile)) { throw "missing $keyFile" }

$bytes = [IO.File]::ReadAllBytes("$dist\YaMumMember.exe")
$sha = -join ([Security.Cryptography.SHA256]::Create().ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') })

$p = New-Object Security.Cryptography.CspParameters 24
$p.Flags = 'UseMachineKeyStore'
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider $p
$rsa.PersistKeyInCsp = $false
$rsa.FromXmlString([IO.File]::ReadAllText($keyFile))
$sig = [Convert]::ToBase64String($rsa.SignData($bytes, 'SHA256'))

$manifest = [ordered]@{
  version   = $version
  url       = 'YaMumMember.exe'
  sha256    = $sha
  signature = $sig
  notes     = $Notes
  published = (Get-Date).ToString('yyyy-MM-dd HH:mm')
}
[IO.File]::WriteAllText("$dist\update.json", ($manifest | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))

if ($Target) {
  New-Item -ItemType Directory -Force $Target | Out-Null
  # exe first, then update.json, so clients never see a manifest without its file
  Copy-Item "$dist\YaMumMember.exe" (Join-Path $Target 'YaMumMember.exe') -Force
  Copy-Item "$dist\update.json" (Join-Path $Target 'update.json') -Force
  Write-Host "Copied to $Target"
}
Write-Host "Published version $version -> $dist"
