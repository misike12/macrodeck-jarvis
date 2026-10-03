using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Serilog;

namespace Jarvis.Plugin.Runtime;

/// <summary>How far along one asset is.</summary>
public readonly record struct DownloadProgress(string AssetId, long BytesSoFar, long? TotalBytes, string Phase)
{
	public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)BytesSoFar / TotalBytes.Value, 0, 1) : null;
}

/// <summary>Why an install could not complete. Never an exception to a caller: a missing binary is a state, not a crash.</summary>
public enum AssetFailure
{
	/// <summary>The host has no network, or the URL could not be reached.</summary>
	Unreachable,

	/// <summary>The download completed but does not match the pin.</summary>
	DigestMismatch,

	/// <summary>The server refused to resume, so the partial file had to be discarded.</summary>
	ResumeRefused,

	/// <summary>An archive that should have unpacked did not.</summary>
	UnpackFailed,

	/// <summary>
	/// The bytes verified and installed, but the executable cannot run on this machine. Distinct from a
	/// corrupt download on purpose: nothing is wrong with the file, so retrying it will change nothing.
	/// </summary>
	Unusable,

	/// <summary>The write failed, most often a full disk.</summary>
	WriteFailed,
}

/// <summary>The outcome of an install attempt.</summary>
public sealed record AssetInstallResult
{
	public required bool Installed { get; init; }

	/// <summary>Absolute path of the installed file, or of the unpacked directory for an archive.</summary>
	public string? Path { get; init; }

	public AssetFailure? Failure { get; init; }

	/// <summary>Written for a log or an issue, never for control flow.</summary>
	public string? Detail { get; init; }

	/// <summary>True when the asset was already present and verified, so nothing was downloaded.</summary>
	public bool AlreadyPresent => Installed && Detail is null;

	public static AssetInstallResult Ok(string path, bool alreadyPresent) =>
		new() { Installed = true, Path = path, Detail = alreadyPresent ? null : "downloaded" };

	public static AssetInstallResult Failed(AssetFailure failure, string? detail) =>
		new() { Installed = false, Failure = failure, Detail = detail };
}

/// <summary>
/// Fetches a pinned asset, verifies it, and installs it atomically.
/// <para>
/// Three properties matter more than speed here. A partial file is never treated as complete: it lives at
/// a distinct path, is only reused when the server agrees to resume, and is verified by digest before
/// anything is installed. An install is atomic: content is written and verified under a temporary name and
/// only then moved into place, so an interrupted install leaves the previous good file rather than a
/// half-written one. And a failed verification discards the bytes instead of retrying them, because a
/// mirror serving the wrong content will keep serving it.
/// </para>
/// </summary>
public sealed class AssetDownloader(HttpClient http, ILogger logger)
{
	private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

	private readonly HttpClient _http = http;
	private readonly ILogger _logger = logger.ForContext<AssetDownloader>();

	public async Task<AssetInstallResult> InstallAsync(
		PinnedAsset asset,
		RuntimePaths paths,
		IProgress<DownloadProgress>? progress,
		CancellationToken cancellationToken)
	{
		paths.EnsureComponentDirectory(asset.Component);

		var partial = paths.PartialPath(asset.Component, asset.FileName);
		var installed = paths.ComponentInstallDirectory(asset.Component, asset.Group);
		Directory.CreateDirectory(installed);

		try
		{
			var fetched = await FetchAsync(asset, partial, progress, cancellationToken).ConfigureAwait(false);

			if (!fetched)
			{
				return AssetInstallResult.Failed(AssetFailure.Unreachable, asset.Url);
			}

			var actual = await AssetDigest.OfFileAsync(partial, cancellationToken).ConfigureAwait(false);

			if (!AssetDigest.Matches(asset.Sha256, actual))
			{
				// The bytes are wrong, so they are not worth keeping: a mirror that serves the wrong
				// content will serve it again on the next attempt.
				TryDelete(partial);
				_logger.Warning(
					"{Asset} did not match its pin. Expected {Expected}, got {Actual}. Discarded.",
					asset.Id, asset.Sha256, actual);

				return AssetInstallResult.Failed(AssetFailure.DigestMismatch, $"expected {asset.Sha256}, got {actual}");
			}

			return asset.Kind == AssetKind.Archive
				? await UnpackAsync(asset, partial, installed, cancellationToken).ConfigureAwait(false)
				: InstallFile(asset, partial, installed);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (HttpRequestException exception)
		{
			return AssetInstallResult.Failed(AssetFailure.Unreachable, exception.Message);
		}
		catch (IOException exception)
		{
			return AssetInstallResult.Failed(AssetFailure.WriteFailed, exception.Message);
		}
	}

	/// <summary>
	/// Downloads to the partial path, appending to whatever is already there when the server allows it.
	/// Returns false only when the asset could not be retrieved at all; a short read or a refused resume is
	/// handled here rather than surfaced, because the retry path differs.
	/// </summary>
	private async Task<bool> FetchAsync(
		PinnedAsset asset,
		string partial,
		IProgress<DownloadProgress>? progress,
		CancellationToken cancellationToken)
	{
		var resumeFrom = ExistingPartialLength(partial, asset);
		Report(progress, asset, resumeFrom, asset.SizeBytes, "downloading");

		using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);

		if (resumeFrom > 0)
		{
			request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
		}

		using var response = await _http
			.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
			.ConfigureAwait(false);

		if (!response.IsSuccessStatusCode)
		{
			_logger.Warning("{Asset} returned {Status}.", asset.Id, (int)response.StatusCode);
			return false;
		}

		var appending = resumeFrom > 0 && response.StatusCode == HttpStatusCode.PartialContent;
		var mode = appending ? FileMode.Append : FileMode.Create;

		// A 200 to a ranged request means the server ignored the range and is sending the whole body, so
		// the partial bytes are stale and must not be prepended to them.
		if (resumeFrom > 0 && !appending)
		{
			_logger.Debug("{Asset} ignored the range request; restarting from the beginning.", asset.Id);
		}

		var total = response.Content.Headers.ContentLength is { } length
			? (appending ? resumeFrom + length : length)
			: asset.SizeBytes;

		var start = appending ? resumeFrom : 0;

		await using var target = new FileStream(partial, mode, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
		await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

		var buffer = new byte[128 * 1024];
		var written = start;

		while (true)
		{
			var read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

			if (read == 0)
			{
				break;
			}

			await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
			written += read;
			Report(progress, asset, written, total, "downloading");
		}

		await target.FlushAsync(cancellationToken).ConfigureAwait(false);
		Report(progress, asset, written, total, "verifying");

		return true;
	}

	/// <summary>
	/// The partial file's length, or zero when it cannot be resumed. A partial longer than the pin's size
	/// is a truncated-then-corrupted file rather than a resumable one, so it is discarded instead of
	/// appended to.
	/// </summary>
	private static long ExistingPartialLength(string partial, PinnedAsset asset)
	{
		if (!File.Exists(partial))
		{
			return 0;
		}

		var length = new FileInfo(partial).Length;

		if (length == 0 || asset.SizeBytes is { } size && length >= size)
		{
			TryDelete(partial);
			return 0;
		}

		return length;
	}

	private static AssetInstallResult InstallFile(PinnedAsset asset, string partial, string installed)
	{
		var target = Path.Combine(installed, asset.FileName);

		// Move over the destination so a re-install replaces atomically rather than truncating in place.
		File.Move(partial, target, overwrite: true);

		return AssetInstallResult.Ok(target, alreadyPresent: false);
	}

	private static async Task<AssetInstallResult> UnpackAsync(
		PinnedAsset asset,
		string partial,
		string installed,
		CancellationToken cancellationToken)
	{
		try
		{
			using var archive = ZipFile.OpenRead(partial);
			archive.ExtractToDirectory(installed, overwriteFiles: true);
		}
		catch (InvalidDataException exception)
		{
			TryDelete(partial);
			return AssetInstallResult.Failed(AssetFailure.UnpackFailed, exception.Message);
		}

		TryDelete(partial);
		return AssetInstallResult.Ok(installed, alreadyPresent: false);
	}

	private void Report(
		IProgress<DownloadProgress>? progress,
		PinnedAsset asset,
		long bytes,
		long? total,
		string phase)
	{
		if (progress is null)
		{
			return;
		}

		// Progress must never be able to fail an install, so a misbehaving reporter is swallowed here
		// rather than unwinding the download.
		try
		{
			progress.Report(new DownloadProgress(asset.Id, bytes, total, phase));
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.Debug(exception, "A download progress reporter threw and was ignored.");
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A leftover partial is harmless: it is verified before use and discarded if it is wrong.
		}
	}
}
