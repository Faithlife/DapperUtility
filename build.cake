#addin "nuget:https://www.nuget.org/api/v2/?package=Cake.Git&version=0.10.0"
#addin "nuget:https://www.nuget.org/api/v2/?package=Octokit&version=0.23.0"
#tool "nuget:https://www.nuget.org/api/v2/?package=coveralls.io&version=1.3.4"
#tool "nuget:https://www.nuget.org/api/v2/?package=NUnit.ConsoleRunner&version=3.5.0"
#tool "nuget:https://www.nuget.org/api/v2/?package=OpenCover&version=4.6.519"
#tool "nuget:https://www.nuget.org/api/v2/?package=ReportGenerator&version=2.5.0"

using LibGit2Sharp;

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var nugetApiKey = Argument("nugetApiKey", "");
var githubApiKey = Argument("githubApiKey", "");
var coverallsApiKey = Argument("coverallsApiKey", "");

var solutionFileName = "DapperUtility.sln";
var githubOwner = "Faithlife";
var githubRepo = "DapperUtility";
var githubRawUri = "http://raw.githubusercontent.com";
var nugetSource = "https://www.nuget.org/api/v2/package";
var coverageAssemblies = new[] { "Faithlife.Utility.Dapper" };

var dotnetFileNames = new HashSet<string> { "global.json", "project.json", "project.lock.json" };

var gitRepository = new LibGit2Sharp.Repository(MakeAbsolute(Directory(".")).FullPath);

var githubClient = new Octokit.GitHubClient(new Octokit.ProductHeaderValue("build.cake"));
if (!string.IsNullOrWhiteSpace(githubApiKey))
	githubClient.Credentials = new Octokit.Credentials(githubApiKey);

Task("Clean")
	.Does(() =>
	{
		CleanDirectories($"src/**/bin");
		CleanDirectories($"src/**/obj");
		CleanDirectories($"tests/**/bin");
		CleanDirectories($"tests/**/obj");
		CleanDirectories("release");
	});

Task("MSBuild")
	.IsDependentOn("Clean")
	.Does(() =>
	{
		foreach (var dotnetFile in GetFiles("**/*.json").Where(x => dotnetFileNames.Contains(x.Segments.Last())))
			System.IO.File.Move(dotnetFile.FullPath, dotnetFile.ChangeExtension(".dotnet").FullPath);

		NuGetRestore(solutionFileName);
		MSBuild(solutionFileName, settings => settings.SetConfiguration(configuration));
	});

Task("MSTest")
	.IsDependentOn("MSBuild")
	.Does(() =>
	{
		NUnit3($"tests/**/bin/**/*.Tests.dll", new NUnit3Settings { NoResults = true });
	});

Task("Coverage")
	.IsDependentOn("MSBuild")
	.Does(() =>
	{
		CreateDirectory("release");
		if (FileExists("release/coverage.xml"))
			DeleteFile("release/coverage.xml");

		string filter = string.Concat(coverageAssemblies.Select(x => $@" ""-filter:+[{x}]*"""));

		foreach (var testDllPath in GetFiles($"./tests/**/bin/**/*.Tests.dll"))
		{
			ExecuteProcess(@"cake\OpenCover\tools\OpenCover.Console.exe",
				$@"-register:user -mergeoutput ""-target:cake\NUnit.ConsoleRunner\tools\nunit3-console.exe"" ""-targetargs:{testDllPath} --noresult"" ""-output:release\coverage.xml"" -skipautoprops -returntargetcode" + filter);
		}
	});

Task("CoverageReport")
	.IsDependentOn("Coverage")
	.Does(() =>
	{
		ExecuteProcess(@"cake\ReportGenerator\tools\ReportGenerator.exe", $@"""-reports:release\coverage.xml"" ""-targetdir:release\coverage""");
	});

Task("CoveragePublish")
	.IsDependentOn("Coverage")
	.Does(() =>
	{
		if (coverallsApiKey.Length != 0)
			ExecuteProcess(@"cake\coveralls.io\tools\coveralls.net.exe", $@"--opencover ""release\coverage.xml"" --full-sources --repo-token {coverallsApiKey}");
		else
			Information("coverallsApiKey is blank; skipping publish.");
	});

Task("DotNetBuild")
	.IsDependentOn("Clean")
	.Does(() =>
	{
		foreach (var dotnetFile in GetFiles("./**/*.dotnet"))
			System.IO.File.Move(dotnetFile.FullPath, dotnetFile.ChangeExtension(".json").FullPath);

		ExecuteProcess("dotnet", "restore");

		foreach (var projectFile in GetFiles("./**/project.json"))
			ExecuteProcess("dotnet", $"build \"{projectFile.FullPath}\" --configuration {configuration}");
	});

Task("DotNetTest")
	.IsDependentOn("DotNetBuild")
	.Does(() =>
	{
		foreach (var projectFile in GetFiles("tests/**/project.json"))
			ExecuteProcess("dotnet", $"test \"{projectFile.FullPath}\" --configuration {configuration} --no-build --noresult");
	});

Task("NuGetPackage")
	.IsDependentOn("DotNetTest")
	.Does(() =>
	{
		if (configuration != "Release")
			throw new InvalidOperationException("Configuration should be Release.");

		CreateDirectory("release/nuget");
		CleanDirectory("release/nuget");

		foreach (var projectFile in GetFiles("./src/**/project.json"))
			ExecuteProcess("dotnet", $"pack \"{projectFile.FullPath}\" --configuration {configuration} --no-build --output release/nuget");
	});

Task("NuGetPublish")
	.IsDependentOn("NuGetPackage")
	.Does(() =>
	{
		if (string.IsNullOrWhiteSpace(nugetApiKey))
			throw new InvalidOperationException("Requires -nugetApiKey=(key)");
		if (string.IsNullOrWhiteSpace(githubApiKey))
			throw new InvalidOperationException("Requires -githubApiKey=(key)");

		var dirtyEntry = gitRepository.RetrieveStatus().FirstOrDefault(x => x.State != FileStatus.Unaltered && x.State != FileStatus.Ignored);
		if (dirtyEntry != null)
			throw new InvalidOperationException($"The git working directory must be clean, but '{dirtyEntry.FilePath}' is dirty.");

		var headSha = gitRepository.Head.Tip.Sha;
		try
		{
			githubClient.Repository.Commit.GetSha1(githubOwner, githubRepo, headSha).GetAwaiter().GetResult();
		}
		catch (Octokit.NotFoundException exception)
		{
			throw new InvalidOperationException($"The current commit '{headSha}' must be pushed to GitHub.", exception);
		}

		var packageFiles = GetFiles("release/nuget/*.nupkg").Where(x => !x.FullPath.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase)).ToList();
		if (packageFiles.Count == 0)
			throw new InvalidOperationException("No packages found to publish.");
		var packageVersions = packageFiles.Select(x => x.FullPath.Split('.')).Select(x => $"{x[x.Length - 4]}.{x[x.Length - 3]}.{x[x.Length - 2]}").Distinct().ToList();
		if (packageVersions.Count != 1)
			throw new InvalidOperationException($"Multiple package versions found: {string.Join(";", packageVersions)}");
		var packageVersion = packageVersions[0];

		foreach (var packageFile in packageFiles)
		{
			NuGetPush(packageFile, new NuGetPushSettings
			{
				ApiKey = nugetApiKey,
				Source = nugetSource,
			});
		}

		Information($"Creating git tag '{packageVersion}'...");
		githubClient.Git.Reference.Create(githubOwner, githubRepo,
			new Octokit.NewReference($"refs/tags/{packageVersion}", headSha)).GetAwaiter().GetResult();
	});

Task("Default")
	.Does(() =>
	{
		Information("Target required, e.g. -target=CoverageReport");
	});

void ExecuteProcess(string exePath, string arguments)
{
	int exitCode = StartProcess(exePath, arguments);
	if (exitCode != 0)
		throw new InvalidOperationException($"{exePath} failed with exit code {exitCode}.");
}

RunTarget(target);
