#addin "nuget:?package=Cake.Git"
#addin "nuget:?package=Octokit"
#tool "nuget:?package=coveralls.io"
#tool "nuget:?package=NUnit.ConsoleRunner"
#tool "nuget:?package=OpenCover"
#tool "nuget:?package=ReportGenerator"

using LibGit2Sharp;

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var nugetApiKey = Argument("nugetApiKey", "");
var githubApiKey = Argument("githubApiKey", "");
var coverallsApiKey = Argument("coverallsApiKey", "");

var githubOwner = "Faithlife";
var githubRepo = "DapperUtility";

var nugetSource = "https://www.nuget.org/api/v2/package";
var githubRawUri = "http://raw.githubusercontent.com";

var dotnetFileNames = new HashSet<string> { "global.json", "project.json", "project.lock.json" };

var gitRepository = new LibGit2Sharp.Repository(MakeAbsolute(Directory(".")).FullPath);

var githubClient = new Octokit.GitHubClient(new Octokit.ProductHeaderValue("build.cake"));
if (!string.IsNullOrWhiteSpace(githubApiKey))
	githubClient.Credentials = new Octokit.Credentials(githubApiKey);

Task("Clean")
	.Does(() =>
	{
		CleanDirectories($"./src/**/bin/{configuration}");
		CleanDirectories($"./src/**/obj/{configuration}");
		CleanDirectories($"./tests/**/bin/{configuration}");
		CleanDirectories($"./tests/**/obj/{configuration}");
	});

Task("MSBuild")
	.IsDependentOn("Clean")
	.Does(() =>
	{
		var solutionFiles = GetFiles("./*.sln");
		if (solutionFiles.Count != 1)
			throw new InvalidOperationException($"{solutionFiles.Count} .sln files found.");
		var solutionFile = solutionFiles.Single();

		foreach (var dotnetFile in GetFiles("./**/*.json").Where(x => dotnetFileNames.Contains(x.Segments.Last())))
			System.IO.File.Move(dotnetFile.FullPath, dotnetFile.ChangeExtension(".dotnet").FullPath);

		NuGetRestore(solutionFile);
		MSBuild(solutionFile, settings => settings.SetConfiguration(configuration));
	});

Task("MSTest")
	.IsDependentOn("MSBuild")
	.Does(() =>
	{
		NUnit3($"./tests/**/bin/{configuration}/*.Tests.dll", new NUnit3Settings { NoResults = true });
	});

Task("Coverage")
	.IsDependentOn("MSBuild")
	.Does(() =>
	{
		CreateDirectory("./build/coverage");
		CleanDirectory("./build/coverage");

		foreach (var testDllPath in GetFiles($"./tests/**/bin/{configuration}/*.Tests.dll"))
		{
			StartProcess(@"tools\OpenCover\tools\OpenCover.Console.exe",
				$@"-register:user -mergeoutput ""-target:tools\NUnit.ConsoleRunner\tools\nunit3-console.exe"" ""-targetargs:{testDllPath} --noresult"" ""-output:build\coverage\coverage.xml"" -skipautoprops -returntargetcode ""-filter:+[Faithlife*]*""");
		}
	});

Task("CoverageReport")
	.IsDependentOn("Coverage")
	.Does(() =>
	{
		StartProcess(@"tools\ReportGenerator\tools\ReportGenerator.exe", $@"""-reports:build\coverage\coverage.xml"" ""-targetdir:build\coverage\report""");
	});

Task("CoveragePublish")
	.IsDependentOn("Coverage")
	.Does(() =>
	{
		if (string.IsNullOrWhiteSpace(coverallsApiKey))
			throw new InvalidOperationException("Requires -coverallsApiKey=(key)");

		StartProcess(@"tools\coveralls.io\tools\coveralls.net.exe", $@"--opencover ""build\coverage\coverage.xml"" --full-sources --repo-token {coverallsApiKey}");
	});

Task("DotNetBuild")
	.IsDependentOn("Clean")
	.Does(() =>
	{
		foreach (var dotnetFile in GetFiles("./**/*.dotnet"))
			System.IO.File.Move(dotnetFile.FullPath, dotnetFile.ChangeExtension(".json").FullPath);

		StartProcess("dotnet", "restore");

		foreach (var projectFile in GetFiles("./**/project.json"))
			StartProcess("dotnet", $"build \"{projectFile.FullPath}\" --configuration {configuration}");
	});

Task("DotNetTest")
	.IsDependentOn("DotNetBuild")
	.Does(() =>
	{
		foreach (var projectFile in GetFiles("./tests/**/project.json"))
			StartProcess("dotnet", $"test \"{projectFile.FullPath}\" --configuration {configuration} --no-build --noresult");
	});

Task("NuGetPackage")
	.IsDependentOn("DotNetTest")
	.Does(() =>
	{
		if (configuration != "Release")
			throw new InvalidOperationException("Configuration should be Release.");

		CreateDirectory("./build/nuget");
		CleanDirectory("./build/nuget");

		foreach (var projectFile in GetFiles("./src/**/project.json"))
			StartProcess("dotnet", $"pack \"{projectFile.FullPath}\" --configuration {configuration} --no-build --output build/nuget");
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

		var packageFiles = GetFiles("./build/nuget/*.nupkg").Where(x => !x.FullPath.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase)).ToList();
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
		Information("Target required, e.g. -target=Coverage");
	});

RunTarget(target);
