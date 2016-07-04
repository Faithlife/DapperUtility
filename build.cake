#addin "Cake.Git"
#tool "nuget:?package=gitlink"
#tool "nuget:?package=NUnit.ConsoleRunner"
#r "System.Net.Http"

using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var nugetSource = Argument("nugetSource", "https://www.nuget.org/api/v2/package");
var nugetApiKey = Argument("nugetApiKey", "");
var githubApiKey = Argument("githubApiKey", "");

var solutionPath = "./DapperUtility.sln";
var nugetPackageName = "Faithlife.Utility.Dapper";
var assemblyPath = $"./src/Faithlife.Utility.Dapper/bin/{configuration}/Faithlife.Utility.Dapper.dll";
var githubApiUri = "https://api.github.com";
var githubRawUri = "http://raw.githubusercontent.com";
var githubOwner = "Faithlife";
var githubRepo = "DapperUtility";

var httpClient = new HttpClient();
httpClient.DefaultRequestHeaders.Add("User-Agent", "build.cake");

string headSha = null;
string version = null;

string GetSemVerFromFile(string path)
{
	var versionInfo = FileVersionInfo.GetVersionInfo(path);
	return $"{versionInfo.FileMajorPart}.{versionInfo.FileMinorPart}.{versionInfo.FileBuildPart}";
}

void VerifyHttpResponse(HttpResponseMessage httpResponse, string description)
{
	if (!httpResponse.IsSuccessStatusCode)
		throw new InvalidOperationException($"{description} failed with {httpResponse.StatusCode}: {httpResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()}");
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

		var httpResponse = httpClient.GetAsync($"{githubApiUri}/repos/{githubOwner}/{githubRepo}/commits/{headSha}").GetAwaiter().GetResult();
		VerifyHttpResponse(httpResponse, $"Finding current commit {headSha} at GitHub");

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
		var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{githubApiUri}/repos/{githubOwner}/{githubRepo}/git/refs");
		httpRequest.Headers.Authorization = AuthenticationHeaderValue.Parse($"token {githubApiKey}");
		httpRequest.Content = new StringContent($"{{\"ref\":\"refs/tags/{tagName}\",\"sha\":\"{headSha}\"}}", Encoding.UTF8, "application/json");
		var httpResponse = httpClient.SendAsync(httpRequest).GetAwaiter().GetResult();
		VerifyHttpResponse(httpResponse, $"GitHub tag creation at {headSha}");
	});

Task("NuGetPublish")
	.IsDependentOn("NuGetPublishOnly")
	.IsDependentOn("NuGetTagOnly");

Task("Default")
	.IsDependentOn("Test");

RunTarget(target);
