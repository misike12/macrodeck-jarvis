using System.Text.Json.Serialization;

namespace Jarvis.Plugin.Runtime;

/// <summary>What a pinned asset is for. Decides how it is installed and whether a digest covers the archive or its contents.</summary>
public enum AssetKind
{
	/// <summary>A downloadable archive that is unpacked into its component directory.</summary>
	Archive,

	/// <summary>A single file installed as-is.</summary>
	File,
}

/// <summary>
/// One pinned, downloadable file. The digest is what makes the catalogue a pin rather than a wish: a
/// download that does not hash to <see cref="Sha256"/> is discarded rather than installed, so a
/// compromised or truncated mirror cannot become a binary JARVIS runs.
/// </summary>
public sealed record PinnedAsset
{
	public required string Id { get; init; }

	public required string Component { get; init; }

	public required string Url { get; init; }

	public required string Sha256 { get; init; }

	public required string FileName { get; init; }

	public AssetKind Kind { get; init; } = AssetKind.File;

	/// <summary>Bytes, when the pin records it. Used only to report progress, never to decide completeness.</summary>
	public long? SizeBytes { get; init; }

	/// <summary>
	/// Which directory inside its component this asset unpacks into. Defaults to the asset's own id, which
	/// is right for a standalone file and wrong for anything that has to sit beside another file: a voice's
	/// ONNX model and its JSON config share a group, because a config in a different directory is a config
	/// the synthesiser will never find.
	/// </summary>
	public string InstallGroup { get; init; } = string.Empty;

	/// <summary>Human-readable purpose, shown in an issue when the asset cannot be fetched.</summary>
	public required string Purpose { get; init; }

	/// <summary>
	/// Arguments that make an unpacked executable print its usage and exit successfully, run as a liveness
	/// probe after install. Hash verification proves the bytes are the ones that were pinned; it says
	/// nothing about whether those bytes can *run* on this machine. A release build compiled for a newer
	/// instruction set installs perfectly and then dies with STATUS_ILLEGAL_INSTRUCTION on first use, which
	/// is the worst possible moment to discover it. The probe turns that into an issue at install time.
	/// </summary>
	public IReadOnlyList<string> ProbeArguments { get; init; } = [];

	/// <summary>Path inside the unpacked archive of the executable to probe, if it holds one.</summary>
	public string? ProbeExecutable { get; init; }

	internal string Group => string.IsNullOrWhiteSpace(InstallGroup) ? Id : InstallGroup;
}

/// <summary>
/// Everything JARVIS can download, pinned. Nothing here is bundled and nothing is fetched until something
/// asks for it, so a fresh install starts with an empty runtime directory and still answers
/// <c>/_macrodeck/health</c>.
/// </summary>
public static class AssetCatalog
{
	public const string Piper = "piper";
	public const string Whisper = "whisper";
	public const string Porcupine = "porcupine";

	/// <summary>
	/// Digests measured from the pinned URLs rather than copied from a release note, because a pin nobody
	/// verified is worse than no pin: it fails closed and the failure looks like a corrupt download.
	/// Re-measure with <c>Get-FileHash -Algorithm SHA256</c> when a version is bumped.
	/// </summary>
	public static IReadOnlyList<PinnedAsset> All { get; } =
	[
		new()
		{
			Id = "piper-windows-amd64",
			Component = Piper,
			FileName = "piper_windows_amd64.zip",
			Url = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip",
			Sha256 = "f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea",
			Kind = AssetKind.Archive,
			SizeBytes = 22477236,
			ProbeExecutable = "piper/piper.exe",
			ProbeArguments = ["--help"],
			Purpose = "Local neural text to speech.",
		},

		new()
		{
			Id = "piper-voice-en-gb-alan-medium",
			Component = Piper,
			FileName = "en_GB-alan-medium.onnx",
			Url = "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_GB/alan/medium/en_GB-alan-medium.onnx",
			Sha256 = "0a309668932205e762801f1efc2736cd4b0120329622adf62be09e56339d3330",
			SizeBytes = 63201294,
			InstallGroup = "voice-en_GB-alan-medium",
			Purpose = "The voice JARVIS speaks with when Piper is selected.",
		},

		new()
		{
			Id = "piper-voice-en-gb-alan-medium-config",
			Component = Piper,
			FileName = "en_GB-alan-medium.onnx.json",
			Url = "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_GB/alan/medium/en_GB-alan-medium.onnx.json",
			Sha256 = "c0f0d124e5895c00e7c03b35dcc8287f319a6998a365b182deb5c8e752ee8c1e",
			SizeBytes = 4888,
			InstallGroup = "voice-en_GB-alan-medium",
			Purpose = "Piper needs the voice's configuration beside the voice.",
		},

		// whisper.cpp 1.7.6 through 1.8.x all crash with STATUS_ILLEGAL_INSTRUCTION on an older x64 CPU:
		// their release builds are compiled for a newer instruction set than such a machine has. 1.9.2 was
		// the first build verified to run here, and the probe below is what keeps a future bump honest.
		new()
		{
			Id = "whisper-bin-x64",
			Component = Whisper,
			FileName = "whisper-bin-x64.zip",
			Url = "https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.2/whisper-bin-x64.zip",
			Sha256 = "49dcc16de826f20bd53d44f947a1ae49dfa81f86cad67a64d80820cb192d674a",
			Kind = AssetKind.Archive,
			SizeBytes = 8194445,
			ProbeExecutable = "Release/whisper-cli.exe",
			ProbeArguments = ["--help"],
			Purpose = "Local speech to text.",
		},

		new()
		{
			Id = "whisper-model-tiny-en",
			Component = Whisper,
			FileName = "ggml-tiny.en.bin",
			Url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin",
			Sha256 = "921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f",
			SizeBytes = 77704715,
			Purpose = "The smallest Whisper model, for a fast first install.",
		},
	];

	public static PinnedAsset? Find(string id) =>
		All.FirstOrDefault(asset => string.Equals(asset.Id, id, StringComparison.OrdinalIgnoreCase));

	public static IReadOnlyList<PinnedAsset> ForComponent(string component) =>
		[.. All.Where(asset => string.Equals(asset.Component, component, StringComparison.OrdinalIgnoreCase))];

	public static IReadOnlyList<string> Components { get; } =
		[.. All.Select(asset => asset.Component).Distinct(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>
/// One installed asset as recorded on disk. The manifest is a cache of what was verified, never the
/// authority: a file that hashes correctly is installed whether or not the manifest mentions it, and one
/// that does not is removed whether or not it is listed.
/// </summary>
public sealed record InstalledAsset
{
	[JsonPropertyName("id")]
	public required string Id { get; init; }

	[JsonPropertyName("component")]
	public required string Component { get; init; }

	[JsonPropertyName("sha256")]
	public required string Sha256 { get; init; }

	[JsonPropertyName("path")]
	public required string Path { get; init; }

	[JsonPropertyName("bytes")]
	public required long Bytes { get; init; }

	[JsonPropertyName("installedAt")]
	public required DateTimeOffset InstalledAt { get; init; }
}

/// <summary>A synthesiser voice that is actually on disk, model and config together.</summary>
/// <param name="Name">The voice's name without extension, which is what the settings store holds.</param>
/// <param name="ModelPath">The ONNX model.</param>
/// <param name="ConfigPath">The model's JSON config, which carries the phoneme map and sample rate.</param>
public sealed record InstalledVoice(string Name, string ModelPath, string ConfigPath);

/// <summary>What the runtime directory currently holds.</summary>
public sealed record RuntimeManifest
{
	[JsonPropertyName("version")]
	public int Version { get; init; } = 1;

	[JsonPropertyName("assets")]
	public IReadOnlyList<InstalledAsset> Assets { get; init; } = [];
}
