using System.Diagnostics;
using Serilog;

namespace Jarvis.Plugin.Runtime;

/// <summary>The reason an installed component could not be executed on this machine.</summary>
public enum ProbeFailure
{
	/// <summary>The executable is not where the catalogue says it unpacked to.</summary>
	Missing,

	/// <summary>
	/// The executable ran and failed. On Windows a negative exit code is the native status word, so
	/// 0xC0000015 reads here as STATUS_ILLEGAL_INSTRUCTION: the binary is intact but was built for a
	/// newer instruction set than this processor has.
	/// </summary>
	Crashed,

	/// <summary>The probe did not finish in time.</summary>
	Hung,
}

/// <summary>
/// Runs an installed executable once to prove it can actually run here.
/// <para>
/// Digest verification answers "are these the bytes that were pinned?" and nothing else. The two are easy
/// to confuse and the difference decides whether a feature works at all: a release build compiled for a
/// newer instruction set hashes perfectly and then dies with an illegal-instruction fault the first time
/// anyone asks it to do real work. Probing at install time turns that into an issue with a plain
/// explanation, instead of an assistant that appears installed and silently fails forever.
/// </para>
/// </summary>
public sealed class ExecutableProbe(ILogger logger)
{
	/// <summary>
	/// Long enough for a native tool to load its runtime, short enough not to look hung. Nothing here does
	/// real work, so a slow probe means something is wrong rather than merely busy.
	/// </summary>
	private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

	private readonly ILogger _logger = logger.ForContext<ExecutableProbe>();

	public async Task<ProbeFailure?> ProbeAsync(PinnedAsset asset, string root, CancellationToken cancellationToken)
	{
		if (asset.ProbeExecutable is not { } relative)
		{
			// Nothing to run: a model file is data, not a program.
			return null;
		}

		var executable = Path.Combine([root, .. relative.Split('/', '\\')]);

		if (!File.Exists(executable))
		{
			_logger.Warning("{Asset} unpacked without {Executable}.", asset.Id, relative);
			return ProbeFailure.Missing;
		}

		var info = new ProcessStartInfo
		{
			FileName = executable,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WindowStyle = ProcessWindowStyle.Hidden,
			WorkingDirectory = Path.GetDirectoryName(executable)!,
		};

		foreach (var argument in asset.ProbeArguments)
		{
			info.ArgumentList.Add(argument);
		}

		using var process = Process.Start(info);
		if (process is null)
		{
			return ProbeFailure.Missing;
		}

		// Both pipes are drained concurrently with the wait. A tool that prints its usage will fill one
		// buffer and block forever if the other is only read afterwards.
		var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

		using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		budget.CancelAfter(Budget);

		try
		{
			await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			TryKill(process);
			return ProbeFailure.Hung;
		}

		await stdout.ConfigureAwait(false);
		await stderr.ConfigureAwait(false);

		if (process.ExitCode == 0)
		{
			return null;
		}

		_logger.Warning(
			"{Asset} installed correctly but cannot run here. Exit {Exit} (0x{ExitHex:X8}).",
			asset.Id, process.ExitCode, unchecked((uint)process.ExitCode));

		return ProbeFailure.Crashed;
	}

	private static void TryKill(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
		{
			// The process has already gone, which is the outcome that was wanted.
		}
	}
}