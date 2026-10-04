using Jarvis.Plugin.Runtime;
using NUnit.Framework;
using Serilog;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Exercises the manager against the real pinned URLs. It is <c>[Explicit]</c> because it moves tens of
/// megabytes over a real network, but it is the only check that the catalogue's digests are correct: a
/// digest that does not match its URL fails closed, which looks to a user exactly like a corrupt download
/// and would never be caught by a loopback test.
/// </summary>
[TestFixture]
[Explicit("Downloads real components from GitHub and Hugging Face.")]
public class RuntimeLiveDownloadTests
{
	private static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

	[TestCase(AssetCatalog.Whisper)]
	[TestCase(AssetCatalog.Piper)]
	[CancelAfter(600_000)]
	public async Task Every_catalogue_pin_downloads_and_verifies(string component)
	{
		var root = Path.Combine(Path.GetTempPath(), $"jarvis-live-{Guid.CreateVersion7():N}");

		try
		{
			using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
			var manager = new RuntimeManager(client, RuntimeTestLog.Logger, new RuntimePaths(root));
			using var cancellation = new CancellationTokenSource(Budget);

			var reports = new List<DownloadProgress>();
			var result = await manager.EnsureComponentAsync(
				component,
				new CollectingProgress<DownloadProgress>(reports.Add),
				cancellation.Token);

			Assert.That(result.Installed, Is.True, $"{component}: {result.Failure} {result.Detail}");
			Assert.That(reports, Is.Not.Empty, "no progress was reported for a real download");

			var installed = AssetCatalog.ForComponent(component);

			foreach (var asset in installed)
			{
				Assert.That(
					await manager.IsInstalledAsync(asset, cancellation.Token),
					Is.True,
					$"{asset.Id} did not verify after install");
			}
		}
		finally
		{
			TryDelete(root);
		}
	}

	/// <summary>
	/// Hashing correctly is not the same as running. Every executable component is probed at install time,
	/// and this asserts the shipped pins survive that probe on this machine, which is where an
	/// instruction-set mismatch between the build machine and the user shows up.
	/// </summary>
	[TestCase(AssetCatalog.Whisper)]
	[TestCase(AssetCatalog.Piper)]
	[CancelAfter(600_000)]
	public async Task Every_pinned_executable_actually_runs_here(string component)
	{
		var root = Path.Combine(Path.GetTempPath(), $"jarvis-probe-{Guid.CreateVersion7():N}");

		try
		{
			using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
			var manager = new RuntimeManager(client, RuntimeTestLog.Logger, new RuntimePaths(root));
			using var cancellation = new CancellationTokenSource(Budget);

			var result = await manager.EnsureComponentAsync(
				component, manager.Progress, cancellation.Token);

			Assert.That(
				result.Installed,
				Is.True,
				$"{component} installed but did not run here: {result.Detail}. The pin needs re-measuring "
				+ "against a build that targets this processor.");

			var issues = await manager.GetIssuesAsync(cancellation.Token);
			Assert.That(issues, Is.Empty, $"{component} installed but raised {issues.Count} issue(s)");
		}
		finally
		{
			TryDelete(root);
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			TestContext.Out.WriteLine($"could not clean up {path}: {exception.Message}");
		}
	}
}
