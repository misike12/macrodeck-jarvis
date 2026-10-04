using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jarvis.Plugin.Core;

/// <summary>
/// A bag of settings supplied in code rather than read from the host.
/// <para>
/// This used to read <c>jarvis.settings.json</c> from beside the plugin executable, which existed so a key
/// could be supplied "during early development". It could not: the integration declares
/// <c>RequiresConfiguration</c>, so the host never starts an unconfigured plugin and there was no
/// development path through it either. All the file actually did was provide somewhere for an API key to
/// live in plaintext, inside a directory that is deleted on every plugin update. The type is kept because
/// it is how a test hands a configured store its values, and <see cref="Empty"/> is what production gets.
/// </para>
/// </summary>
public sealed class LocalSettingsFile
{
	private const string FileName = "jarvis.settings.json";

	/// <summary>No settings at all, which is what the plugin runs with outside a test.</summary>
	public static LocalSettingsFile Empty { get; } = new(string.Empty, new FileModel());

	private static readonly JsonSerializerOptions ReadOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	private readonly string _path;
	private readonly FileModel _model;

	private LocalSettingsFile(string path, FileModel model)
	{
		_path = path;
		_model = model;
	}

	/// <summary>
	/// Reads a settings file from a directory. Kept for tests only.
	/// <para>
	/// Nothing in the shipped plugin calls this. It exists so a test can point at a real file without
	/// putting a credential into the test output directory, where every other test in the assembly would then
	/// read it and believe itself configured.
	/// </para>
	/// </summary>
	public static LocalSettingsFile Load(string contentRoot)
	{
		var candidate = Path.Combine(contentRoot, FileName);
		var model = new FileModel();

		if (File.Exists(candidate))
		{
			try
			{
				var json = File.ReadAllText(candidate);
				model = JsonSerializer.Deserialize<FileModel>(json, ReadOptions) ?? new FileModel();
			}
			catch (JsonException)
			{
				model = new FileModel();
			}
			catch (IOException)
			{
				model = new FileModel();
			}
		}

		return new LocalSettingsFile(candidate, model);
	}

	public string? NvidiaApiKey => _model.Nvidia?.ApiKey;

	public string? NvidiaBaseUrl => _model.Nvidia?.BaseUrl;

	public string? SelfHostedBaseUrl => _model.SelfHostedNim?.BaseUrl;

	public string? SelfHostedToken => _model.SelfHostedNim?.Token;

	public string? PicovoiceAccessKey => _model.Picovoice?.AccessKey;

	public string FilePath => _path;

	private sealed class FileModel
	{
		public NvidiaModel? Nvidia { get; set; }

		public SelfHostedModel? SelfHostedNim { get; set; }

		public PicovoiceModel? Picovoice { get; set; }
	}

	private sealed class NvidiaModel
	{
		public string? ApiKey { get; set; }

		public string? BaseUrl { get; set; }
	}

	private sealed class SelfHostedModel
	{
		public string? BaseUrl { get; set; }

		public string? Token { get; set; }
	}

	private sealed class PicovoiceModel
	{
		[JsonPropertyName("accessKey")]
		public string? AccessKey { get; set; }
	}
}