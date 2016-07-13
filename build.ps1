<#
.SYNOPSIS
This is a Powershell script to bootstrap a Cake build.
#>

[CmdletBinding()]
Param(
    [Parameter(Position=0,Mandatory=$false,ValueFromRemainingArguments=$true)]
    [string[]]$ScriptArgs
)

# download nuget.exe if not in path and not already downloaded
$ToolsDirPath = Join-Path $PSScriptRoot "tools"
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
Invoke-Expression "&`"$NuGetExePath`" install -ExcludeVersion -OutputDirectory `"$ToolsDirPath`""
If ($LASTEXITCODE -ne 0) {
    Throw "An error occured while restoring NuGet tools."
}
Pop-Location

# run Cake with specified arguments
$CakeExePath = Join-Path $ToolsDirPath "Cake/Cake.exe"
Invoke-Expression "& `"$CakeExePath`" -experimental $ScriptArgs"
Exit $LASTEXITCODE
