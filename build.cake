#tool "nuget:?package=XmlDocMarkdown&version=0.5.0"
#tool "nuget:?package=xunit.runner.console&version=2.2.0"

using System.Text.RegularExpressions;

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var nugetApiKey = Argument("nugetApiKey", "");
var trigger = Argument("trigger", "");

var solutionFileName = "DapperUtility.sln";
var nugetSource = "https://api.nuget.org/v3/index.json";
var docsAssembly = $@"src\Faithlife.Utility.Dapper\bin\{configuration}\net45\Faithlife.Utility.Dapper.dll";
var docsSourceUri = "https://github.com/Faithlife/DapperUtility/tree/master/src/Faithlife.Utility.Dapper";

Task("Clean")
	.Does(() =>
	{
		CleanDirectories("src/**/bin");
		CleanDirectories("src/**/obj");
		CleanDirectories("tests/**/bin");
		CleanDirectories("tests/**/obj");
		CleanDirectories("release");
	});

Task("Build")
	.IsDependentOn("Clean")
	.Does(() =>
	{
		DotNetCoreRestore(solutionFileName);
		DotNetCoreBuild(solutionFileName, new DotNetCoreBuildSettings { Configuration = configuration, ArgumentCustomization = args => args.Append("--verbosity normal") });
	});

Task("GenerateDocs")
	.IsDependentOn("Build")
	.Does(() => GenerateDocs(verify: false));

Task("VerifyGenerateDocs")
	.IsDependentOn("Build")
	.Does(() => GenerateDocs(verify: true));

Task("Test")
	.IsDependentOn("VerifyGenerateDocs")
	.Does(() =>
	{
		foreach (var projectPath in GetFiles("tests/**/*.csproj").Select(x => x.FullPath))
			DotNetCoreTest(projectPath, new DotNetCoreTestSettings { Configuration = configuration });
	});

Task("NuGetPackage")
	.IsDependentOn("Test")
	.Does(() =>
	{
		foreach (var projectPath in GetFiles("src/**/*.csproj").Select(x => x.FullPath))
			DotNetCorePack(projectPath, new DotNetCorePackSettings { Configuration = configuration, OutputDirectory = "release" });
	});

Task("NuGetPublish")
	.IsDependentOn("NuGetPackage")
	.WithCriteria(() => !string.IsNullOrEmpty(nugetApiKey))
	.Does(() =>
	{
		var nupkgPaths = GetFiles("release/*.nupkg").Select(x => x.FullPath).ToList();

		string version = null;
		foreach (var nupkgPath in nupkgPaths)
		{
			string nupkgVersion = Regex.Match(nupkgPath, @"\.([^\.]+\.[^\.]+\.[^\.]+)\.nupkg$").Groups[1].ToString();
			if (version == null)
				version = nupkgVersion;
			else if (version != nupkgVersion)
				throw new InvalidOperationException($"Mismatched package versions '{version}' and '{nupkgVersion}'.");
		}

		if (trigger == null || Regex.IsMatch(trigger, "^v[0-9]"))
		{
			if (trigger != null && trigger != $"v{version}")
				throw new InvalidOperationException($"Trigger '{trigger}' doesn't match package version '{version}'.");

			var pushSettings = new NuGetPushSettings { ApiKey = nugetApiKey, Source = nugetSource };
			foreach (var nupkgPath in nupkgPaths)
				NuGetPush(nupkgPath, pushSettings);
		}
		else
		{
			Information("To publish this package, push this git tag: v" + version);
		}
	});

Task("Default")
	.IsDependentOn("Test");

void GenerateDocs(bool verify)
{
	int exitCode = StartProcess($@"cake\XmlDocMarkdown\tools\XmlDocMarkdown.exe",
		$@"{docsAssembly} docs\ --source ""{docsSourceUri}"" --newline lf --clean" + (verify ? " --verify" : ""));
	if (exitCode == 1 && verify)
		throw new InvalidOperationException("Generated docs don't match; use -target=GenerateDocs to regenerate.");
	else if (exitCode != 0)
		throw new InvalidOperationException($"Docs generation failed with exit code {exitCode}.");
}

void ExecuteProcess(string exePath, string arguments)
{
	int exitCode = StartProcess(exePath, arguments);
	if (exitCode != 0)
		throw new InvalidOperationException($"{exePath} failed with exit code {exitCode}.");
}

RunTarget(target);
