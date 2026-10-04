using System.Text.Json;
using MacroDeck.Localization;
using MacroDeck.Sdk.Issues;
using Serilog;

namespace Jarvis.Plugin.Runtime;

/// <summary>
/// Installs and keeps track of the native components JARVIS can use.
/// <para>
/// Nothing here runs at start-up. A plugin that must answer <c>/_macrodeck/health</c> with an empty
/// runtime directory cannot also reach for the network while doing it, so every install is demanded by
/// something that actually needs the component. A component that is missing, corrupt or unreachable is
/// reported as an issue the user can act on, and the caller falls back rather than failing.
/// </para>
/// </summary>
public sealed class RuntimeManager : IIntegrationIssueProvider
{
	private const int ManifestVersion = 1;

	private static readonly Lock ManifestGate = new();

	private readonly HttpClient _http;
	private readonly AssetDownloader _downloader;
	private readonly ExecutableProbe _probe;
	private readonly RuntimePaths _paths;
	private readonly ILogger _logger;

	/// <summary>The logger this manager was built with, for a caller that has no other.</summary>
	public ILogger Logger => _logger;
	private readonly Lock _gate = new();
	private readonly Dictionary<string, FailedAsset> _failures = [];

	private readonly RuntimeProgressReporter _progress = new();

	private RuntimeManifest _manifest = new();

	public RuntimeManager(HttpClient http, ILogger logger, RuntimePaths? paths = null)
	{
		_http = http;
		_logger = logger.ForContext<RuntimeManager>();
		_paths = paths ?? RuntimePaths.Resolve();
		_downloader = new AssetDownloader(http, _logger);
		_probe = new ExecutableProbe(_logger);
		_manifest = ReadManifest();
	}

	public RuntimePaths Paths => _paths;

	/// <summary>Progress as an <see cref="IProgress{T}"/> the downloader can report into.</summary>
	public IProgress<DownloadProgress> Progress => _progress;

	/// <summary>The latest reported progress, for a variable read or a widget opened mid-download.</summary>
	public RuntimeProgress CurrentProgress => _progress.Current;

	/// <summary>Raised whenever progress changes.</summary>
	public event Action<RuntimeProgress>? ProgressChanged
	{
		add => _progress.Changed += value;
		remove => _progress.Changed -= value;
	}

	/// <summary>
	/// Installs an asset the caller already holds. This is the primary entry point: the catalogue is a
	/// convenience for the common case, not a gate, so a caller that has a verified asset in hand is not
	/// forced to smuggle it through a global lookup.
	/// </summary>
	public async Task<AssetInstallResult> EnsureAsync(
		PinnedAsset asset,
		IProgress<DownloadProgress>? progress,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(asset);

		if (await IsInstalledAsync(asset, cancellationToken).ConfigureAwait(false))
		{
			Remember(asset, InstalledPath(asset), alreadyPresent: true);
			_progress.Finish(asset.Id);
			return AssetInstallResult.Ok(InstalledPath(asset), alreadyPresent: true);
		}

		var tracker = new Progress<DownloadProgress>(report =>
		{
			progress?.Report(report);
			_progress.Report(report);
		});

		var result = await _downloader.InstallAsync(asset, _paths, tracker, cancellationToken).ConfigureAwait(false);

		if (result.Installed && result.Path is { } path)
		{
			// A component that installs cleanly but cannot execute is worse than one that never installed,
			// because it looks available until the first real request. The bytes are kept, not discarded:
			// this is a property of the machine, and a different machine may run them perfectly.
			if (await _probe.ProbeAsync(asset, InstalledPath(asset), cancellationToken).ConfigureAwait(false)
				is { } probeFailure)
			{
				RecordFailure(asset, AssetFailure.Unusable, DescribeProbe(probeFailure));
				return AssetInstallResult.Failed(AssetFailure.Unusable, DescribeProbe(probeFailure));
			}

			Remember(asset, path, alreadyPresent: false);
		}
		else
		{
			Forget(asset.Id);
			RecordFailure(asset, result.Failure ?? AssetFailure.WriteFailed, result.Detail);
		}

		_progress.Finish(asset.Id);
		return result;
	}

	/// <summary>Installs a catalogue asset by id.</summary>
	public async Task<AssetInstallResult> EnsureAsync(
		string assetId,
		IProgress<DownloadProgress>? progress,
		CancellationToken cancellationToken)
	{
		if (AssetCatalog.Find(assetId) is not { } asset)
		{
			return AssetInstallResult.Failed(AssetFailure.WriteFailed, $"'{assetId}' is not a pinned asset.");
		}

		return await EnsureAsync(asset, progress, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Installs every asset a component needs. The first failure stops the component: a voice without its
	/// config, or a binary without its model, is not a partial success worth reporting as one.
	/// </summary>
	public async Task<AssetInstallResult> EnsureComponentAsync(
		string component,
		IProgress<DownloadProgress>? progress,
		CancellationToken cancellationToken)
	{
		var assets = AssetCatalog.ForComponent(component);

		if (assets.Count == 0)
		{
			return AssetInstallResult.Failed(AssetFailure.WriteFailed, $"'{component}' is not a known component.");
		}

		foreach (var asset in assets)
		{
			var result = await EnsureAsync(asset.Id, progress, cancellationToken).ConfigureAwait(false);

			if (!result.Installed)
			{
				return result;
			}
		}

		return AssetInstallResult.Ok(_paths.ComponentDirectory(component), alreadyPresent: true);
	}

	/// <summary>Whether an asset is on disk and still hashes to its pin.</summary>
	public async Task<bool> IsInstalledAsync(PinnedAsset asset, CancellationToken cancellationToken)
	{
		var path = InstalledPath(asset);

		if (asset.Kind == AssetKind.Archive)
		{
			// An archive is verified before it is unpacked, so the manifest record is the evidence. The
			// archive itself is deleted afterwards, leaving nothing left to re-hash.
			return Recorded(asset.Id)?.Sha256 is { } digest && AssetDigest.Matches(asset.Sha256, digest);
		}

		if (!File.Exists(path))
		{
			return false;
		}

		try
		{
			return AssetDigest.Matches(asset.Sha256, await AssetDigest.OfFileAsync(path, cancellationToken).ConfigureAwait(false));
		}
		catch (IOException)
		{
			return false;
		}
	}

	/// <summary>The file a component runs, or null when its asset is not installed.</summary>
	public string? ExecutablePath(string assetId)
	{
		var asset = AssetCatalog.Find(assetId);

		return asset is null || Recorded(assetId) is null ? null : InstalledPath(asset);
	}

	/// <summary>
	/// Where an installed asset lives, or null when it is not installed. Callers that need to run something
	/// resolve it here rather than recomputing the layout, so a change to where assets land is one change.
	/// </summary>
	public string? AssetPath(string assetId) =>
		AssetCatalog.Find(assetId) is { } asset && Recorded(assetId) is not null ? InstalledPath(asset) : null;

	/// <summary>
	/// A file inside an unpacked archive. Piper's release unpacks to a nested <c>piper/</c> folder holding
	/// the executable, its DLLs and the espeak data, so the caller needs to reach into what was unpacked
	/// rather than treat the archive as a single file.
	/// </summary>
	public string? UnpackedFile(string assetId, params string[] relativeSegments)
	{
		if (AssetPath(assetId) is not { } root)
		{
			return null;
		}

		var path = Path.Combine([root, .. relativeSegments]);

		return File.Exists(path) ? path : null;
	}

	/// <summary>
	/// Files matching a pattern inside a component directory. Used to discover what is actually installed,
	/// such as which speech models a user has, rather than asking a caller to track paths itself.
	/// </summary>
	public IReadOnlyList<string> InstalledFiles(string component, string searchPattern)
	{
		var directory = _paths.ComponentDirectory(component);

		return Directory.Exists(directory)
			? [.. Directory.EnumerateFiles(directory, searchPattern, SearchOption.AllDirectories)]
			: [];
	}

	/// <summary>
	/// The voices actually on disk. A voice is a matched <c>.onnx</c> and <c>.onnx.json</c> pair: the
	/// config carries the phoneme map and the sample rate, so an <c>.onnx</c> without one cannot be
	/// spoken. Listing only real pairs is what lets the voice dropdown be populated from disk instead of
	/// from a hardcoded list that drifts.
	/// </summary>
	public IReadOnlyList<InstalledVoice> InstalledVoices(string component)
	{
		var directory = _paths.ComponentDirectory(component);

		if (!Directory.Exists(directory))
		{
			return [];
		}

		var voices = new List<InstalledVoice>();

		foreach (var model in Directory.EnumerateFiles(directory, "*.onnx", SearchOption.AllDirectories))
		{
			var config = Path.ChangeExtension(model, ".onnx.json");

			if (File.Exists(config))
			{
				voices.Add(new InstalledVoice(Path.GetFileNameWithoutExtension(model), model, config));
			}
		}

		return [.. voices.OrderBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)];
	}

	/// <summary>
	/// One derivation for the issue id, so the failure table is keyed by exactly the id the host will call
	/// back with. Keying by asset id and prefixing only on the way out makes every retry a silent no-op.
	/// </summary>
	private static string IssueIdFor(PinnedAsset asset) => $"runtime-{asset.Id}";

	/// <summary>
	/// A failure remembers the asset it belongs to, not just a message. A retry therefore reinstalls
	/// exactly what failed, rather than looking the asset up again and being unable to find an id that was
	/// never in the catalogue.
	/// </summary>
	/// <summary>
	/// A failure remembers the asset it belongs to and how many times it has been seen, not just a message.
	/// The count is what lets the issue say "tried twice" rather than leaving the user to guess whether a
	/// retry was ever worth anything, and a retry therefore reinstalls exactly what failed.
	/// </summary>
	private sealed record FailedAsset(PinnedAsset Asset, AssetFailure Failure, string? Detail, int Attempts);

	public Task<IReadOnlyList<IntegrationIssue>> GetIssuesAsync(CancellationToken cancellationToken = default)
	{
		lock (_gate)
		{
			return Task.FromResult<IReadOnlyList<IntegrationIssue>>(
			[
				.. _failures.Values.Select(failure => Describe(failure)),
			]);
		}
	}

	public Task<IssueResolution> ResolveIssueAsync(string issueId, CancellationToken cancellationToken = default)
	{
		FailedAsset? failure;

		lock (_gate)
		{
			if (!_failures.Remove(issueId, out failure) || failure is null)
			{
				// Reported as a failure rather than a success. An id that was never reported is a stale widget
				// or a plugin that has been restarted, and answering "resolved" tells the host the condition is
				// gone when nothing was ever checked.
				return Task.FromResult(IssueResolution.Failed(Strings.Runtime.Issue.UnknownIssue()));
			}
		}

		// The asset goes in the message template, not in a structured property. Properties reach the live log
		// viewer but are not written to the log file, so the persisted record was "Retrying after the user
		// asked." with the one thing that identifies the retry missing.
		_logger.Information("Retrying {Asset} after the user asked.", issueId);
		return RetryAsync(failure.Asset, cancellationToken);
	}

	private async Task<IssueResolution> RetryAsync(PinnedAsset asset, CancellationToken cancellationToken)
	{
		var result = await EnsureAsync(asset, progress: null, cancellationToken).ConfigureAwait(false);

		return result.Installed
			? IssueResolution.Ok()
			: IssueResolution.Failed(Strings.Runtime.IssueStillFailing());
	}

	/// <summary>
	/// A component that cannot be fetched is a state the user can see and retry, not an error that escapes
	/// into whoever happened to ask for it. The description carries what the component was for and what
	/// actually went wrong, because those are the two things worth acting on; the URL is not, because
	/// there is nothing a user can do with a GitHub path.
	/// </summary>
	private static IntegrationIssue Describe(FailedAsset failure)
	{
		var (title, description, severity) = failure.Failure switch
		{
			AssetFailure.DigestMismatch or AssetFailure.UnpackFailed =>
				(Strings.Runtime.Issue.Digest.Title(), Strings.Runtime.Issue.Digest.Description(), IntegrationIssueSeverity.Error),
			AssetFailure.Unusable =>
				(Strings.Runtime.Issue.Unusable.Title(), Strings.Runtime.Issue.Unusable.Description(), IntegrationIssueSeverity.Error),
			_ => (Strings.Runtime.Issue.Unreachable.Title(), Strings.Runtime.Issue.Unreachable.Description(), IntegrationIssueSeverity.Warning),
		};

		return new IntegrationIssue
		{
			Id = IssueIdFor(failure.Asset),
			Title = title,
			// Composed rather than welded into one template. The three fragments used to be a single key with
			// three placeholders and spaces between them, which fixes the word order: a language that puts
			// the reason last, or needs a different connective, could not be expressed at all.
			Description = ComposeIssueDescription(description, failure),
			ActionLabel = Strings.Runtime.Issue.Action(),
			Severity = severity,
		};
	}

	/// <summary>
	/// The issue body: what happened, what it was for, and what the provider said.
	/// <para>
	/// Each part is its own sentence with its own key, so a translation can order them as its language
	/// requires and drop any part that does not apply, rather than being handed three fragments to glue
	/// together in whatever order the English happened to use.
	/// </para>
	/// </summary>
	private static string ComposeIssueDescription(LocalizedText reason, FailedAsset failure)
	{
		var text = Strings.Runtime.Issue.Detail(reason).ToString();

		if (!string.IsNullOrWhiteSpace(failure.Asset.Purpose))
		{
			text += " " + Strings.Runtime.Issue.Purpose(failure.Asset.Purpose);
		}

		if (!string.IsNullOrWhiteSpace(failure.Detail))
		{
			text += " " + Strings.Runtime.Issue.TechnicalDetail(failure.Detail);
		}

		// Counted rather than asserted. "Tried once" for something that has failed four times is the kind of
		// sentence that makes a user stop believing the rest of the message.
		text += " " + Strings.Runtime.Issue.Retried(failure.Attempts);

		return text;
	}

	/// <summary>
	/// Diagnostic detail for the issue, deliberately English: it names a native status and a file, which
	/// is log output rather than something to translate. The user's-facing explanation is the issue's
	/// title and description.
	/// </summary>
	private static string DescribeProbe(ProbeFailure failure) => failure switch
	{
		ProbeFailure.Missing => "the download unpacked without the program it should contain",
		ProbeFailure.Hung => "the program did not start within twenty seconds",
		_ => "the program stopped immediately with STATUS_ILLEGAL_INSTRUCTION (0xC0000015)",
	};

	/// <summary>
	/// Where an asset's contents land. Two assets sharing an install group share this directory, which is
	/// how a voice's model and its config end up beside each other rather than in two folders that no
	/// synthesiser would ever pair up.
	/// </summary>
	private string InstalledPath(PinnedAsset asset) => asset.Kind == AssetKind.Archive
		? _paths.ComponentInstallDirectory(asset.Component, asset.Group)
		: Path.Combine(_paths.ComponentInstallDirectory(asset.Component, asset.Group), asset.FileName);

	private InstalledAsset? Recorded(string assetId)
	{
		lock (_gate)
		{
			return _manifest.Assets.FirstOrDefault(entry =>
				string.Equals(entry.Id, assetId, StringComparison.OrdinalIgnoreCase));
		}
	}

	private void Remember(PinnedAsset asset, string path, bool alreadyPresent)
	{
		if (alreadyPresent && Recorded(asset.Id) is not null)
		{
			return;
		}

		InstalledAsset? entry = null;

		if (File.Exists(path))
		{
			entry = new InstalledAsset
			{
				Id = asset.Id,
				Component = asset.Component,
				Sha256 = asset.Sha256,
				Path = path,
				Bytes = new FileInfo(path).Length,
				InstalledAt = DateTimeOffset.UtcNow,
			};
		}
		else if (asset.Kind == AssetKind.Archive && Directory.Exists(path))
		{
			entry = new InstalledAsset
			{
				Id = asset.Id,
				Component = asset.Component,
				Sha256 = asset.Sha256,
				Path = path,
				Bytes = 0,
				InstalledAt = DateTimeOffset.UtcNow,
			};
		}

		if (entry is null)
		{
			return;
		}

		lock (_gate)
		{
			_manifest = _manifest with
			{
				Assets =
				[
					.. _manifest.Assets.Where(existing =>
						!string.Equals(existing.Id, asset.Id, StringComparison.OrdinalIgnoreCase)),
					entry,
				],
			};

			_failures.Remove(IssueIdFor(asset));
			WriteManifest(_manifest);
		}
	}

	private void Forget(string assetId)
	{
		lock (_gate)
		{
			_manifest = _manifest with
			{
				Assets = [.. _manifest.Assets.Where(entry =>
					!string.Equals(entry.Id, assetId, StringComparison.OrdinalIgnoreCase))],
			};

			WriteManifest(_manifest);
		}
	}

	private void RecordFailure(PinnedAsset asset, AssetFailure failure, string? detail)
	{
		lock (_gate)
		{
			_failures[IssueIdFor(asset)] = _failures.TryGetValue(IssueIdFor(asset), out var seen)
				? seen with { Attempts = seen.Attempts + 1 }
				: new FailedAsset(asset, failure, detail, 1);
		}

		_logger.Warning("{Asset} is unavailable. {Failure}: {Detail}", asset.Id, failure, detail);
	}

	private RuntimeManifest ReadManifest()
	{
		try
		{
			if (!File.Exists(_paths.ManifestPath))
			{
				return new RuntimeManifest { Version = ManifestVersion };
			}

			var manifest = JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllText(_paths.ManifestPath));

			return manifest is null || manifest.Version != ManifestVersion
				? new RuntimeManifest { Version = ManifestVersion }
				: manifest;
		}
		catch (Exception exception) when (exception is IOException or JsonException)
		{
			// An unreadable manifest is treated as an empty one. Re-downloading something already present is
			// wasteful but harmless; trusting a manifest that may be truncated is not.
			_logger.Warning(exception, "The runtime manifest could not be read and was treated as empty.");
			return new RuntimeManifest { Version = ManifestVersion };
		}
	}

	private void WriteManifest(RuntimeManifest manifest)
	{
		try
		{
			Directory.CreateDirectory(_paths.Root);

			var temporary = _paths.ManifestPath + ".tmp";
			File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, JsonOptions));

			// The manifest is a cache, so a half-written one is only a wasted re-download, but writing it
			// atomically still costs nothing.
			File.Move(temporary, _paths.ManifestPath, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_logger.Warning(exception, "The runtime manifest could not be written.");
		}
	}

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
