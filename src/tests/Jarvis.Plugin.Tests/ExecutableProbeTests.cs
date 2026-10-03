using System.Runtime.Versioning;
using Jarvis.Plugin.Runtime;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The probe's job is to catch a component that installs perfectly and then cannot run. Every one of its
/// outcomes is checked here against a real process, because a probe that only ever returns "fine" is
/// indistinguishable from no probe at all.
/// </summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public class ExecutableProbeTests
{
	private string _root = null!;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), $"jarvis-probe-test-{Guid.CreateVersion7():N}");
		Directory.CreateDirectory(_root);
	}

	[TearDown]
	public void TearDown()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// A leftover temp directory is not worth failing a test over.
		}
	}

	[Test]
	public async Task An_asset_with_nothing_to_run_is_not_probed()
	{
		var probe = new ExecutableProbe(RuntimeTestLog.Logger);

		var failure = await probe.ProbeAsync(
			new PinnedAsset
			{
				Id = "model-only",
				Component = "test",
				FileName = "model.bin",
				Url = "https://example.invalid/model.bin",
				Sha256 = new string('a', 64),
				Purpose = "Data, not a program.",
			},
			_root,
			TestContext.CurrentContext.CancellationToken);

		Assert.That(failure, Is.Null, "a model file was treated as a program and probed");
	}

	[Test]
	public async Task An_archive_that_did_not_unpack_is_reported_as_missing()
	{
		var probe = new ExecutableProbe(RuntimeTestLog.Logger);

		var failure = await probe.ProbeAsync(
			new PinnedAsset
			{
				Id = "no-exe",
				Component = "test",
				FileName = "thing.zip",
				Url = "https://example.invalid/thing.zip",
				Sha256 = new string('a', 64),
				Purpose = "An archive with nothing runnable in it.",
				ProbeExecutable = "Release/tool.exe",
				ProbeArguments = ["--help"],
			},
			_root,
			TestContext.CurrentContext.CancellationToken);

		Assert.That(failure, Is.EqualTo(ProbeFailure.Missing));
	}

	/// <summary>
	/// The real failure this exists to catch: a program that is present, hashes correctly, and exits
	/// immediately with a native status word. <c>cmd.exe</c> stands in for it because it is guaranteed to
	/// be present and can be asked for a chosen exit code.
	/// </summary>
	[Test]
	public async Task A_program_that_exits_non_zero_is_reported_as_crashed()
	{
		var probe = new ExecutableProbe(RuntimeTestLog.Logger);

		var failure = await probe.ProbeAsync(
			new PinnedAsset
			{
				Id = "bad-exe",
				Component = "test",
				FileName = "tool.exe",
				Url = "https://example.invalid/tool.zip",
				Sha256 = new string('a', 64),
				Purpose = "A program that cannot run here.",
				ProbeExecutable = "tool.exe",
				ProbeArguments = ["/c", "exit", "5"],
			},
			CopyCommandInterpreter(),
			TestContext.CurrentContext.CancellationToken);

		Assert.That(failure, Is.EqualTo(ProbeFailure.Crashed));
	}

	[Test]
	public async Task A_program_that_exits_zero_probes_clean()
	{
		var probe = new ExecutableProbe(RuntimeTestLog.Logger);

		var failure = await probe.ProbeAsync(
			new PinnedAsset
			{
				Id = "good-exe",
				Component = "test",
				FileName = "tool.exe",
				Url = "https://example.invalid/tool.zip",
				Sha256 = new string('a', 64),
				Purpose = "A program that runs.",
				ProbeExecutable = "tool.exe",
				ProbeArguments = ["/c", "exit", "0"],
			},
			CopyCommandInterpreter(),
			TestContext.CurrentContext.CancellationToken);

		Assert.That(failure, Is.Null);
	}

	/// <summary>
	/// The probe must survive a program that fills a pipe. A native tool prints its usage, and a probe
	/// that read one stream while waiting on the other would hang here forever.
	/// </summary>
	[Test]
	public async Task A_program_that_floods_its_output_still_probes()
	{
		var probe = new ExecutableProbe(RuntimeTestLog.Logger);

		var failure = await probe.ProbeAsync(
			new PinnedAsset
			{
				Id = "loud-exe",
				Component = "test",
				FileName = "tool.exe",
				Url = "https://example.invalid/tool.zip",
				Sha256 = new string('a', 64),
				Purpose = "A very talkative program.",
				ProbeExecutable = "tool.exe",
				ProbeArguments = ["/c", "for /L %i in (1,1,40000) do @echo aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"],
			},
			CopyCommandInterpreter(),
			TestContext.CurrentContext.CancellationToken);

		Assert.That(failure, Is.Null, "the probe did not drain the child's pipes");
	}

	/// <summary>Copies the real interpreter into the sandbox so the probe runs something with a real exit code.</summary>
	private string CopyCommandInterpreter()
	{
		var source = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

		var target = Path.Combine(_root, "tool.exe");
		File.Copy(source, target, overwrite: true);

		return _root;
	}
}