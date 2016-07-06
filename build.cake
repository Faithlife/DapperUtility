#addin "nuget:?package=Cake.Git"
#addin "nuget:?package=Octokit"
#tool "nuget:?package=gitlink"
#tool "nuget:?package=NUnit.ConsoleRunner"

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var nugetApiKey = Argument("nugetApiKey", "");
var githubApiKey = Argument("githubApiKey", "");

var solutionPath = "./DapperUtility.sln";
var nugetPackageName = "Faithlife.Utility.Dapper";
var assemblyPath = $"./src/Faithlife.Utility.Dapper/bin/{configuration}/Faithlife.Utility.Dapper.dll";
var nugetSource = "https://www.nuget.org/api/v2/package";
var githubRawUri = "http://raw.githubusercontent.com";
var githubOwner = "Faithlife";
var githubRepo = "DapperUtility";

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
		headSha = GitLogTip(Directory(".")).Sha;
		version = GetSemVerFromFile(assemblyPath);

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
	.WithCriteria(() => !string.IsNullOrEmpty(nugetSource) && !string.IsNullOrEmpty(nugetApiKey))
	.Does(() =>
	{
		NuGetPush($"./build/{nugetPackageName}.{version}.nupkg", new NuGetPushSettings
		{
			ApiKey = nugetApiKey,
			Source = nugetSource.Length == 0 ? null : nugetSource,
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

Task("Default")
	.IsDependentOn("Test");

RunTarget(target);
