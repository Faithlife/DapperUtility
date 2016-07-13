#addin "nuget:?package=Cake.Git"
#addin "nuget:?package=Octokit"
#tool "nuget:?package=coveralls.io"
#tool "nuget:?package=gitlink"
#tool "nuget:?package=NUnit.ConsoleRunner"
#tool "nuget:?package=OpenCover"
#tool "nuget:?package=ReportGenerator"

using LibGit2Sharp;

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var nugetApiKey = Argument("nugetApiKey", "");
var githubApiKey = Argument("githubApiKey", "");
var coverallsApiKey = Argument("coverallsApiKey", "");

var solutionPath = "./DapperUtility.sln";
var nugetPackageName = "Faithlife.Utility.Dapper";
var assemblyPath = $"./src/Faithlife.Utility.Dapper/bin/{configuration}/Faithlife.Utility.Dapper.dll";
var githubOwner = "Faithlife";
var githubRepo = "DapperUtility";
var githubRawUri = "http://raw.githubusercontent.com";
var nugetSource = "https://www.nuget.org/api/v2/package";

var gitRepository = new LibGit2Sharp.Repository(MakeAbsolute(Directory(".")).FullPath);

var githubClient = new Octokit.GitHubClient(new Octokit.ProductHeaderValue("build.cake"));
if (!string.IsNullOrEmpty(githubApiKey))
	githubClient.Credentials = new Octokit.Credentials(githubApiKey);

string headSha = null;
string version = null;

string GetSemVerFromFile(string path)
{
	var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
	return $"{versionInfo.FileMajorPart}.{versionInfo.FileMinorPart}.{versionInfo.FileBuildPart}";
}

Task("Clean")
	.Does(() =>
	{
		CleanDirectories($"./src/**/bin/{configuration}");
		CleanDirectories($"./src/**/obj/{configuration}");
		CleanDirectories($"./tests/**/bin/{configuration}");
		CleanDirectories($"./tests/**/obj/{configuration}");
	});

Task("NuGetRestore")
	.IsDependentOn("Clean")
	.Does(() => NuGetRestore(solutionPath));

Task("Build")
	.IsDependentOn("NuGetRestore")
	.Does(() => MSBuild(solutionPath, settings => settings.SetConfiguration(configuration)));

Task("Test")
	.IsDependentOn("Build")
	.Does(() => NUnit3($"./tests/**/bin/{configuration}/*.Tests.dll", new NUnit3Settings { NoResults = true }));

Task("SourceIndex")
	.IsDependentOn("Test")
	.WithCriteria(() => configuration == "Release")
	.Does(() =>
	{
		var dirtyEntry = gitRepository.RetrieveStatus().FirstOrDefault(x => x.State != FileStatus.Unaltered && x.State != FileStatus.Ignored);
		if (dirtyEntry != null)
			throw new InvalidOperationException($"The git working directory must be clean, but '{dirtyEntry.FilePath}' is dirty.");

		headSha = gitRepository.Head.Tip.Sha;
		try
		{
			githubClient.Repository.Commit.GetSha1(githubOwner, githubRepo, headSha).GetAwaiter().GetResult();
		}
		catch (Octokit.NotFoundException exception)
		{
			throw new InvalidOperationException($"The current commit '{headSha}' must be pushed to GitHub.", exception);
		}

		GitLink(MakeAbsolute(Directory(".")).FullPath, new GitLinkSettings
		{
			RepositoryUrl = $"{githubRawUri}/{githubOwner}/{githubRepo}",
		});

		version = GetSemVerFromFile(assemblyPath);
	});

Task("NuGetPack")
	.IsDependentOn("SourceIndex")
	.Does(() =>
	{
		CreateDirectory("./build");
		NuGetPack($"./{nugetPackageName}.nuspec", new NuGetPackSettings
		{
			Version = version,
			ArgumentCustomization = args => args.Append($"-Prop Configuration={configuration}"),
			OutputDirectory = "./build",
		});
	});

Task("NuGetPublishOnly")
	.IsDependentOn("NuGetPack")
	.WithCriteria(() => !string.IsNullOrEmpty(nugetApiKey))
	.Does(() =>
	{
		NuGetPush($"./build/{nugetPackageName}.{version}.nupkg", new NuGetPushSettings
		{
			ApiKey = nugetApiKey,
			Source = nugetSource,
		});
	});

Task("NuGetTagOnly")
	.IsDependentOn("NuGetPack")
	.WithCriteria(() => !string.IsNullOrEmpty(githubApiKey))
	.Does(() =>
	{
		var tagName = $"nuget-{version}";
		Information($"Creating git tag '{tagName}'...");
		githubClient.Git.Reference.Create(githubOwner, githubRepo,
			new Octokit.NewReference($"refs/tags/{tagName}", headSha)).GetAwaiter().GetResult();
	});

Task("NuGetPublish")
	.IsDependentOn("NuGetPublishOnly")
	.IsDependentOn("NuGetTagOnly");

Task("Coverage")
	.IsDependentOn("Build")
	.Does(() =>
	{
		CreateDirectory("./build");
		if (FileExists("./build/coverage.xml"))
			DeleteFile("./build/coverage.xml");
		foreach (var testDllPath in GetFiles($"./tests/**/bin/{configuration}/*.Tests.dll"))
		{
			StartProcess(@"tools\OpenCover\tools\OpenCover.Console.exe",
				$@"-register:user -mergeoutput ""-target:tools\NUnit.ConsoleRunner\tools\nunit3-console.exe"" ""-targetargs:{testDllPath} --noresult"" ""-output:build\coverage.xml"" -skipautoprops -returntargetcode ""-filter:+[Faithlife*]*""");
		}
	});

Task("CoverageReport")
	.IsDependentOn("Coverage")
	.Does(() =>
	{
		StartProcess(@"tools\ReportGenerator\tools\ReportGenerator.exe", $@"""-reports:build\coverage.xml"" ""-targetdir:build\coverage""");
	});

Task("CoveragePublish")
	.IsDependentOn("Coverage")
	.Does(() =>
	{
		StartProcess(@"tools\coveralls.io\tools\coveralls.net.exe", $@"--opencover ""build\coverage.xml"" --full-sources --repo-token {coverallsApiKey}");
	});

Task("Default")
	.IsDependentOn("Test");

RunTarget(target);
