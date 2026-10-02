using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jarvis.Plugin.Core;

/// <summary>
/// Reads the developer settings file that lives beside the plugin executable and nowhere else. It is
/// how a key gets in without a config flow during early development; the integration config flow is the
/// supported path and wins whenever it has a value.
/// </summary>
public sealed class LocalSettingsFile
{
	private const string FileName = "jarvis.settings.json";

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