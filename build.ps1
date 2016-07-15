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

# create packages.config
$PackagesConfigPath = Join-Path $ToolsDirPath "packages.config"
If (!(Test-Path $PackagesConfigPath)) {
    [System.IO.File]::WriteAllLines($PackagesConfigPath, @(
        "<?xml version=`"1.0`" encoding=`"utf-8`"?>",
        "<packages>",
        "`t<package id=`"Cake`" version=`"0.14.0`" />",
        "</packages>"))
}

# download nuget.exe if not in path and not already downloaded
$NuGetExe = Get-Command "nuget.exe" -ErrorAction SilentlyContinue
If ($NuGetExe -ne $null) {
    $NuGetExePath = $NuGetExe.Path
}
Else {
    $NuGetExePath = Join-Path $ToolsDirPath "nuget.exe"
    If (!(Test-Path $NuGetExePath)) {
        Invoke-WebRequest -Uri http://dist.nuget.org/win-x86-commandline/latest/nuget.exe -OutFile $NuGetExePath
    }
}

# use NuGet to download Cake
Push-Location $ToolsDirPath
Invoke-Expression "&`"$NuGetExePath`" install -ExcludeVersion -OutputDirectory ."
If ($LASTEXITCODE -ne 0) {
    Throw "An error occured while restoring NuGet tools."
}
Pop-Location

# run Cake with specified arguments
$CakeExePath = Join-Path $ToolsDirPath "Cake/Cake.exe"
Invoke-Expression "& `"$CakeExePath`" -experimental $ScriptArgs"
Exit $LASTEXITCODE
