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
				new Progress<DownloadProgress>(reports.Add),
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
