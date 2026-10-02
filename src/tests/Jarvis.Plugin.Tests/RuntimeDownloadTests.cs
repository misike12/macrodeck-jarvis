using System.Net;
using System.Security.Cryptography;
using System.Text;
using Jarvis.Plugin.Runtime;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Drives the download manager against a real HTTP server on loopback, because the properties that matter
/// here - resume, refusal to resume, digest rejection, atomic install - only exist in the interaction
/// between the client and a server that answers honestly. A mocked handler would assert the code calls
/// what it already calls.
/// </summary>
[TestFixture]
public sealed class RuntimeDownloadTests
{
	private LoopbackServer _server = null!;
	private string _root = null!;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), $"jarvis-rt-{Guid.CreateVersion7():N}");
		Directory.CreateDirectory(_root);
		_server = LoopbackServer.Start();
	}

	[TearDown]
	public void TearDown()
	{
		_server.Dispose();
		Directory.Delete(_root, recursive: true);
	}

	[Test]
	public async Task A_pinned_file_is_downloaded_verified_and_recorded()
	{
		var payload = RandomPayload(40_000);
		var asset = PublishFile("payload.bin", payload);

		var manager = NewManager();
		var result = await manager.EnsureAsync(asset, progress: null, TestContext.CurrentContext.CancellationToken);

		Assert.Multiple(() =>
		{
			Assert.That(result.Installed, Is.True, Describe(result));
			Assert.That(File.ReadAllBytes(result.Path!), Is.EqualTo(payload));
			Assert.That(_server.Requests, Has.Count.EqualTo(1), string.Join(" | ", _server.Requests));
		});

		var manifest = File.ReadAllText(Path.Combine(_root, "manifest.json"));
		Assert.That(manifest, Does.Contain(asset.Id));
	}

	/// <summary>
	/// A second request must not re-download. This is the check that keeps a deck from pulling 63 MB of
	/// voice on every turn.
	/// </summary>
	[Test]
	public async Task An_installed_asset_is_not_downloaded_again()
	{
		var asset = PublishFile("payload.bin", RandomPayload(20_000));

		var manager = NewManager();
		var cancellationToken = TestContext.CurrentContext.CancellationToken;

		var first = await manager.EnsureAsync(asset, progress: null, cancellationToken);
		var second = await manager.EnsureAsync(asset, progress: null, cancellationToken);

		Assert.Multiple(() =>
		{
			Assert.That(first.Installed, Is.True);
			Assert.That(second.Installed, Is.True);
			Assert.That(second.AlreadyPresent, Is.True);
			Assert.That(_server.Requests, Has.Count.EqualTo(1), "the second call hit the network again");
		});
	}

	/// <summary>
	/// A file that no longer hashes to its pin is not handed back, and is removed: the caller wants a
	/// component it can execute, not a path to whatever is sitting on disk.
	/// </summary>
	[Test]
	public async Task A_file_that_no_longer_matches_its_pin_is_reinstalled()
	{
		var payload = RandomPayload(15_000);
		var asset = PublishFile("payload.bin", payload);

		var manager = NewManager();
		var cancellationToken = TestContext.CurrentContext.CancellationToken;

		var first = await manager.EnsureAsync(asset, progress: null, cancellationToken);
		await File.WriteAllBytesAsync(first.Path!, RandomPayload(15_000), cancellationToken);

		var second = await manager.EnsureAsync(asset, progress: null, cancellationToken);

		Assert.Multiple(() =>
		{
			Assert.That(second.Installed, Is.True, second.Detail);
			Assert.That(File.ReadAllBytes(second.Path!), Is.EqualTo(payload));
			Assert.That(_server.Requests, Has.Count.EqualTo(2));
		});
	}

	/// <summary>
	/// The digest is the authority. Content that does not match is deleted rather than installed, because
	/// a mirror serving the wrong bytes will keep serving them and the next attempt would too.
	/// </summary>
	[Test]
	public async Task Content_that_does_not_match_the_pin_is_discarded()
	{
		var asset = PublishFile("payload.bin", RandomPayload(9_000)) with { Sha256 = new string('a', 64) };

		var manager = NewManager();
		var result = await manager.EnsureAsync(asset, progress: null, TestContext.CurrentContext.CancellationToken);

		Assert.Multiple(() =>
		{
			Assert.That(result.Installed, Is.False);
			Assert.That(result.Failure, Is.EqualTo(AssetFailure.DigestMismatch));
		});

		var partial = Path.Combine(_root, asset.Component, asset.FileName + ".part");
		Assert.That(File.Exists(partial), Is.False, "the rejected bytes were kept");
	}

	/// <summary>
	/// The resume path is the one that is easy to get subtly wrong, so it is tested at the byte level: the
	/// server sees a Range header, the client sends only the remainder, and the result is identical to a
	/// single full download.
	/// </summary>
	[Test]
	public async Task An_interrupted_download_resumes_from_the_offset()
	{
		var payload = RandomPayload(64_000);
		var asset = PublishFile("payload.bin", payload);

		var paths = new RuntimePaths(_root);
		paths.EnsureComponentDirectory(asset.Component);

		var partial = paths.PartialPath(asset.Component, asset.FileName);

		// The first 25,000 bytes are already on disk, exactly as an interrupted download would leave them.
		await File.WriteAllBytesAsync(partial, payload[..25_000], TestContext.CurrentContext.CancellationToken);

		var manager = NewManager();
		var result = await manager.EnsureAsync(asset, progress: null, TestContext.CurrentContext.CancellationToken);

		Assert.Multiple(() =>
		{
			Assert.That(result.Installed, Is.True, result.Detail);
			Assert.That(File.ReadAllBytes(result.Path!), Is.EqualTo(payload));
			Assert.That(_server.Requests, Has.Count.EqualTo(1));
			Assert.That(_server.Requests[0], Does.Contain("bytes=25000-"), "the resume was not requested by offset");
		});
	}

	/// <summary>
	/// A server that ignores Range answers 200 with the whole body. Appending that to the partial file
	/// would silently concatenate two copies, so the client must restart instead.
	/// </summary>
	[Test]
	public async Task A_server_that_ignores_the_range_restarts_the_download()
	{
		var payload = RandomPayload(30_000);
		var asset = PublishFile("payload.bin", payload);

		var paths = new RuntimePaths(_root);
		paths.EnsureComponentDirectory(asset.Component);
		await File.WriteAllBytesAsync(
			paths.PartialPath(asset.Component, asset.FileName), RandomPayload(10_000), TestContext.CurrentContext.CancellationToken);

		var manager = NewManager(ignoreRange: true);
		var result = await manager.EnsureAsync(asset, progress: null, TestContext.CurrentContext.CancellationToken);

		Assert.Multiple(() =>
		{
			Assert.That(result.Installed, Is.True, result.Detail);
			Assert.That(File.ReadAllBytes(result.Path!), Is.EqualTo(payload), "the partial was prepended to a full body");
		});
	}

	/// <summary>A component with nothing installed reports an issue rather than throwing at the caller.</summary>
	[Test]
	public async Task An_unreachable_component_becomes_an_issue_not_an_exception()
	{
		var asset = PublishFile("payload.bin", RandomPayload(1_000));
		_server.FailEverything = true;

		var manager = NewManager();
		var result = await manager.EnsureAsync(asset, progress: null, TestContext.CurrentContext.CancellationToken);

		Assert.That(result.Installed, Is.False);

		var issues = await manager.GetIssuesAsync(TestContext.CurrentContext.CancellationToken);

		Assert.Multiple(() =>
		{
			Assert.That(issues, Has.Count.EqualTo(1));
			Assert.That(issues[0].Id, Is.EqualTo($"runtime-{asset.Id}"));
			Assert.That(issues[0].Severity, Is.EqualTo(MacroDeck.Sdk.Issues.IntegrationIssueSeverity.Warning));
		});
	}

	/// <summary>
	/// A digest failure is a different thing from an unreachable host, and the user can act on it
	/// differently, so it is reported as an error rather than a warning.
	/// </summary>
	[Test]
	public async Task A_digest_failure_is_reported_as_an_error()
	{
		var asset = PublishFile("payload.bin", RandomPayload(1_000)) with { Sha256 = new string('b', 64) };

		var manager = NewManager();
		await manager.EnsureAsync(asset, progress: null, TestContext.CurrentContext.CancellationToken);

		var issues = await manager.GetIssuesAsync(TestContext.CurrentContext.CancellationToken);

		Assert.Multiple(() =>
		{
			Assert.That(issues, Has.Count.EqualTo(1));
			Assert.That(issues[0].Severity, Is.EqualTo(MacroDeck.Sdk.Issues.IntegrationIssueSeverity.Error));
		});
	}

	/// <summary>Resolving an issue is the retry, so a user pressing the button gets another attempt.</summary>
	[Test]
	public async Task Resolving_an_issue_retries_the_download()
	{
		var asset = PublishFile("payload.bin", RandomPayload(2_000));

		var manager = NewManager();
		var cancellationToken = TestContext.CurrentContext.CancellationToken;

		_server.FailEverything = true;
		await manager.EnsureAsync(asset, progress: null, cancellationToken);
		Assert.That(await manager.GetIssuesAsync(cancellationToken), Has.Count.EqualTo(1));

		_server.FailEverything = false;
		var resolution = await manager.ResolveIssueAsync($"runtime-{asset.Id}", cancellationToken);

		var remaining = await manager.GetIssuesAsync(cancellationToken);

		Assert.Multiple(() =>
		{
			Assert.That(resolution.Success, Is.True);
			Assert.That(remaining, Is.Empty, "the issue outlived a successful retry");
		});
	}

	/// <summary>
	/// The plugin has to boot with an empty runtime directory. This asserts that directly, because a
	/// start-up path that reached for the network would break conformance on an offline machine.
	/// </summary>
	[Test]
	public void A_fresh_runtime_directory_starts_empty_and_does_not_throw()
	{
		// A directory of its own: the fixture root exists because the test wrote to it.
		var untouched = Path.Combine(_root, "never-used");
		var manager = new RuntimeManager(
			new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, RuntimeTestLog.Logger, new RuntimePaths(untouched));

		Assert.Multiple(() =>
		{
			Assert.That(Directory.Exists(untouched), Is.False, "constructing the manager created the runtime directory");
			Assert.That(File.Exists(Path.Combine(untouched, "manifest.json")), Is.False);
		});
	}

	/// <summary>Progress is reported with a monotonic byte count, which is what a progress bar needs.</summary>
	[Test]
	public async Task Progress_is_reported_with_a_monotonic_byte_count()
	{
		var payload = RandomPayload(200_000);
		var asset = PublishFile("payload.bin", payload);

		var manager = NewManager();
		var reports = new List<DownloadProgress>();
		var result = await manager.EnsureAsync(
			asset, new Progress<DownloadProgress>(reports.Add), TestContext.CurrentContext.CancellationToken);

		Assert.That(result.Installed, Is.True, Describe(result));
		Assert.That(reports, Is.Not.Empty);

		var bytes = reports.Select(report => report.BytesSoFar).ToList();
		Assert.That(bytes, Is.Ordered, "the byte count went backwards");
		Assert.That(bytes[^1], Is.EqualTo(payload.Length));
	}

	[Test]
	public void A_catalogue_entry_that_names_a_traversal_is_refused()
	{
		var paths = new RuntimePaths(_root);

		Assert.Multiple(() =>
		{
			Assert.Throws<ArgumentException>(() => paths.ComponentDirectory("../escape"));
			Assert.Throws<ArgumentException>(() => paths.ComponentDirectory("a/b"));
		});
	}

	/// <summary>Every pin in the catalogue has to be a well-formed digest, or verification is theatre.</summary>
	[Test]
	public void Every_pinned_asset_carries_a_sha256_and_an_https_url()
	{
		Assert.That(AssetCatalog.All, Is.Not.Empty);

		foreach (var asset in AssetCatalog.All)
		{
			Assert.Multiple(() =>
			{
				Assert.That(asset.Sha256, Has.Length.EqualTo(64), $"{asset.Id} has no digest");
				Assert.That(asset.Sha256, Does.Match("^[0-9a-f]{64}$"), $"{asset.Id} has a malformed digest");
				Assert.That(asset.Url, Does.StartWith("https://"), $"{asset.Id} is not pinned to https");
				Assert.That(asset.Id, Is.Not.Empty);
				Assert.That(asset.Purpose, Is.Not.Empty);
			});
		}
	}

	/// <summary>Two assets sharing a file name in one component would overwrite each other.</summary>
	[Test]
	public void No_component_has_two_assets_with_the_same_file_name()
	{
		var duplicates = AssetCatalog.All
			.GroupBy(asset => $"{asset.Component}/{asset.FileName}".ToLowerInvariant(), StringComparer.Ordinal)
			.Where(group => group.Count() > 1)
			.Select(group => group.Key)
			.ToList();

		Assert.That(duplicates, Is.Empty, $"colliding asset file names: {string.Join(", ", duplicates)}");
	}

	/// <summary>A failure's reason is the whole point of the type, so it belongs in the assertion message.</summary>
	private static string Describe(AssetInstallResult result) =>
		$"installed={result.Installed} failure={result.Failure} detail={result.Detail} path={result.Path}";

	private RuntimeManager NewManager(bool ignoreRange = false)
	{
		_server.IgnoreRange = ignoreRange;

		// A dedicated client rather than a shared one: these tests set their own timeout and must not be
		// perturbed by anything else in the suite.
		var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

		return new RuntimeManager(client, RuntimeTestLog.Logger, new RuntimePaths(_root));
	}

	private PinnedAsset PublishFile(string name, byte[] content)
	{
		var url = _server.Publish(name, content);
		var digest = Convert.ToHexStringLower(SHA256.HashData(content));

		return new PinnedAsset
		{
			Id = $"test-{name.Replace('.', '-')}",
			Component = "test",
			FileName = name,
			Url = url,
			Sha256 = digest,
			SizeBytes = content.Length,
			Purpose = "A test payload.",
		};
	}

	private static byte[] RandomPayload(int length)
	{
		var buffer = new byte[length];
		RandomNumberGenerator.Fill(buffer);

		// Random bytes compress to nothing and can make a zip-based test ambiguous; a mild bias keeps the
		// content incompressible enough to exercise real byte counts.
		return buffer;
	}
}

/// <summary>
/// A real HTTP server, so the client's range handling is tested against a server that can actually choose
/// to honour or ignore the header.
/// </summary>
internal sealed class LoopbackServer : IDisposable
{
	private readonly HttpListener _listener = new();
	private readonly Dictionary<string, byte[]> _files = [];
	private readonly Lock _gate = new();
	private readonly CancellationTokenSource _shutdown = new();

	public List<string> Requests { get; } = [];

	public bool FailEverything { get; set; }

	public bool IgnoreRange { get; set; }

	public static LoopbackServer Start()
	{
		var server = new LoopbackServer();
		server._listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
		server._listener.Start();
		server.Run();

		return server;
	}

	private static int Port { get; } = FreePort();

	private static int FreePort()
	{
		using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
		socket.Start();
		var port = ((IPEndPoint)socket.LocalEndpoint).Port;
		socket.Stop();

		return port;
	}

	public string Publish(string name, byte[] content)
	{
		lock (_gate)
		{
			_files[name] = content;
		}

		return $"http://127.0.0.1:{Port}/{name}";
	}

	private void Run()
	{
		_ = Task.Run(async () =>
		{
			while (!_shutdown.IsCancellationRequested)
			{
				HttpListenerContext context;

				try
				{
					context = await _listener.GetContextAsync().ConfigureAwait(false);
				}
				catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
				{
					return;
				}

				_ = Task.Run(() => Respond(context));
			}
		});
	}

	private void Respond(HttpListenerContext context)
	{
		var request = context.Request;
		var name = request.Url!.AbsolutePath.Trim('/');
		string rangeHeader;

		lock (_gate)
		{
			Requests.Add($"{request.HttpMethod} {request.Url!.AbsolutePath} {rangeHeader = request.Headers["Range"] ?? "-"}");
		}

		if (FailEverything)
		{
			context.Response.StatusCode = 503;
			context.Response.Close();
			return;
		}

		byte[] content;

		lock (_gate)
		{
			if (!_files.TryGetValue(name, out var found))
			{
				context.Response.StatusCode = 404;
				context.Response.Close();
				return;
			}

			content = found;
		}

		var offset = 0;

		if (!IgnoreRange && rangeHeader is { } header && header.StartsWith("bytes=", StringComparison.Ordinal))
		{
			offset = int.Parse(header["bytes=".Length..].Split('-')[0], System.Globalization.CultureInfo.InvariantCulture);
			context.Response.StatusCode = 206;
			context.Response.AddHeader("Content-Range", $"bytes {offset}-{content.Length - 1}/{content.Length}");
		}
		else
		{
			context.Response.StatusCode = 200;
		}

		context.Response.ContentLength64 = content.Length - offset;
		context.Response.OutputStream.Write(content, offset, content.Length - offset);
		context.Response.OutputStream.Close();
	}

	public void Dispose()
	{
		_shutdown.Cancel();

		try
		{
			_listener.Stop();
			_listener.Close();
		}
		catch (ObjectDisposedException)
		{
			// Already torn down.
		}

		_shutdown.Dispose();
	}
}

internal static class RuntimeTestLog
{
	public static ILogger Logger { get; } = new LoggerConfiguration()
		.MinimumLevel.Debug()
		.WriteTo.Sink(RuntimeNoopSink.Instance)
		.CreateLogger();
}

internal sealed class RuntimeNoopSink : ILogEventSink
{
	public static RuntimeNoopSink Instance { get; } = new();

	public void Emit(LogEvent logEvent)
	{
		if (logEvent.Exception is { } exception)
		{
			TestContext.Out.WriteLine($"  log[{logEvent.Level}] {logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture)} {exception.Message}");
		}
	}
}
