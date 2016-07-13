<#
.SYNOPSIS
This is a Powershell script to bootstrap a Cake build.
#>

[CmdletBinding()]
Param(
    [Parameter(Position=0,Mandatory=$false,ValueFromRemainingArguments=$true)]
    [string[]]$ScriptArgs
)

# create tools directory
$ToolsDirPath = Join-Path $PSScriptRoot "tools"
New-Item -Path $ToolsDirPath -Type Directory -ErrorAction SilentlyContinue | Out-Null

# download nuget.exe if not in path and not already downloaded
$NuGetExe = Get-Command "nuget.exe" -ErrorAction SilentlyContinue
if ($NuGetExe -ne $null) {
    $NuGetExePath = $NuGetExe.Path
}
Else {
    $NuGetExePath = Join-Path $ToolsDirPath "nuget.exe"
    if (!(Test-Path $NuGetExePath)) {
        Invoke-WebRequest -Uri http://dist.nuget.org/win-x86-commandline/latest/nuget.exe -OutFile $NuGetExePath
    }
}

# download packages.config if necessary
$PackagesConfigPath = Join-Path $ToolsDirPath "packages.config"
if (!(Test-Path $PackagesConfigPath)) {
    Invoke-WebRequest -Uri http://cakebuild.net/download/bootstrapper/packages -OutFile $PackagesConfigPath
}

# download Cake via NuGet and packages.config
Push-Location $ToolsDirPath
Invoke-Expression "&`"$NuGetExePath`" install -ExcludeVersion -OutputDirectory `"$ToolsDirPath`""
if ($LASTEXITCODE -ne 0) {
    Throw "An error occured while restoring NuGet tools."
}
Pop-Location

# run Cake with specified arguments
$CakeExePath = Join-Path $ToolsDirPath "Cake/Cake.exe"
Invoke-Expression "& `"$CakeExePath`" -experimental $ScriptArgs"
exit $LASTEXITCODE
