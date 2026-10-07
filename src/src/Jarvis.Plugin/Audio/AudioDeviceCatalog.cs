using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

namespace Jarvis.Plugin.Audio;

[SupportedOSPlatform("windows")]
public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Enumerates capture and render endpoints and resolves the configured one. Selection is by id first and
/// by substring against the friendly name second, because endpoint indices are assigned by Windows and
/// change between reboots.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AudioDeviceCatalog
{
	public static IReadOnlyList<AudioDevice> CaptureDevices() => Devices(DataFlow.Capture);

	public static IReadOnlyList<AudioDevice> RenderDevices() => Devices(DataFlow.Render);

	/// <summary>
	/// Falls back rather than failing: a microphone that was unplugged should degrade to the default, not
	/// take the orb down.
	/// </summary>
	public static AudioDevice? ResolveCapture(string? preferredId, string? preferredName)
	{
		var devices = CaptureDevices();

		if (devices.Count == 0)
		{
			return null;
		}

		if (!string.IsNullOrWhiteSpace(preferredId))
		{
			var byId = devices.FirstOrDefault(device =>
				string.Equals(device.Id, preferredId, StringComparison.OrdinalIgnoreCase));

			if (byId is not null)
			{
				return byId;
			}

			Log.Information("Configured capture device {Device} is gone; falling back.", preferredId);
		}

		if (!string.IsNullOrWhiteSpace(preferredName))
		{
			var byName = devices.FirstOrDefault(device =>
				device.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase));

			if (byName is not null)
			{
				return byName;
			}
		}

		return devices.FirstOrDefault(device => device.IsDefault) ?? devices[0];
	}

	/// <summary>
	/// Opens a capture device at whatever format it will actually give us.
	/// <para>
	/// There is no fixed format. A cheap USB headset wants 48 kHz and converts nothing below it; a studio
	/// interface may only offer 96 or 192 kHz; a Bluetooth headset arrives at 16 or 24 kHz; an aggregate
	/// endpoint reports whatever the driver underneath it settled on. Rather than assume, the ladder below is
	/// tried in order and the endpoint's own format is the last resort, and the format that was actually
	/// agreed is read back off the recorder rather than assumed from the request. Nothing downstream may then
	/// treat the stream as 48 kHz mono, because that is what makes a working microphone sound like noise.
	/// </para>
	/// <para>
	/// Every failure is collected rather than only the last, because "the microphone did not open" with no
	/// reason is the one line in the log that leaves a user with nothing to act on.
	/// </para>
	/// </summary>
	public static bool TryOpenCapture(
		AudioDevice device,
		out WasapiRecorder? recorder,
		out string? error,
		out CaptureFormat format)
	{
		recorder = null;
		error = null;
		format = CaptureFormat.Preferred;

		MMDevice endpoint;

		try
		{
			using var enumerator = new MMDeviceEnumerator();
			endpoint = enumerator.GetDevice(device.Id);
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			error = exception.Message;
			return false;
		}

		var native = NativeFormatOf(endpoint);
		List<string> failures = [];

		foreach (var candidate in CandidatesFor(native))
		{
			try
			{
				var opened = new WasapiRecorderBuilder()
					.WithDevice(endpoint)
					.WithSharedMode()
					.WithPollingSync()
					.WithFormat(candidate.ToWaveFormat())
					.Build();

				// Read back rather than trust the request. A driver is entitled to hand over the format it
				// prefers, and every number downstream depends on this one being true.
				format = CaptureFormat.From(opened.WaveFormat);

				if (!format.IsFloat32)
				{
					// The builder was asked for floats and did not get them. Reading them as floats would be
					// noise, so this is a failure rather than something to work around silently.
					opened.Dispose();
					failures.Add($"{candidate}: the device delivered {format.BitsPerSample}-bit samples");
					continue;
				}

				recorder = opened;
				error = null;
				return true;
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				failures.Add($"{candidate}: {exception.Message}");
			}
		}

		recorder?.Dispose();
		recorder = null;
		error = failures.Count == 0
			? "the capture device could not be opened."
			: string.Join("; ", failures);
		return false;
	}

	/// <summary>
	/// The formats to try, in order, for one endpoint.
	/// <para>
	/// The endpoint's own rate comes first when it is not already on the ladder: a device that only does
	/// 96 kHz is opened at 96 kHz rather than made to fail, and a device that does 48 kHz is not asked for 48
	/// kHz twice. The preferred rates then cover a driver that will convert, and the endpoint's own channel
	/// count and bit depth are tried last for one that will not do any of it.
	/// </para>
	/// </summary>
	internal static IReadOnlyList<CaptureFormat> CandidatesFor(CaptureFormat native)
	{
		List<CaptureFormat> candidates = [];

		if (CaptureRates.IsUsable(native.SampleRate) && !CaptureRates.Preferred.Contains(native.SampleRate))
		{
			candidates.Add(new CaptureFormat(native.SampleRate, CaptureFormat.RequiredChannels, 32));
		}

		foreach (var rate in CaptureRates.Preferred)
		{
			candidates.Add(new CaptureFormat(rate, CaptureFormat.RequiredChannels, 32));
		}

		if (CaptureRates.IsUsable(native.SampleRate)
			&& native.Channels > CaptureFormat.RequiredChannels
			&& native.Channels <= 8)
		{
			candidates.Add(new CaptureFormat(native.SampleRate, native.Channels, 32));
		}

		if (CaptureRates.IsUsable(native.SampleRate) && native.SampleRate != MicrophoneMonitor.SampleRate)
		{
			candidates.Add(native);
		}

		return [.. candidates.Distinct()];
	}

	/// <summary>
	/// The endpoint's own format, or a zeroed one when it will not say. A device that cannot report its
	/// mix format is not an endpoint that has been unplugged, so the conversion attempts still run.
	/// </summary>
	private static CaptureFormat NativeFormatOf(MMDevice endpoint)
	{
		try
		{
			// CreateAudioClient rather than the AudioClient property: the property builds a new client on every
			// read, and this is a one-shot question about the endpoint's own format.
			using var client = endpoint.CreateAudioClient();
			var mix = client.MixFormat;

			return new CaptureFormat(mix.SampleRate, mix.Channels, mix.BitsPerSample);
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			Log.Debug(exception, "The capture endpoint did not report its mix format.");
			return default;
		}
	}

	private static List<AudioDevice> Devices(DataFlow flow)
	{
		try
		{
			using var enumerator = new MMDeviceEnumerator();
			var defaultId = enumerator.GetDefaultAudioEndpoint(flow, Role.Console).ID;
			var collection = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
			var devices = new List<AudioDevice>();

			foreach (var endpoint in collection)
			{
				devices.Add(new AudioDevice(
					endpoint.ID,
					endpoint.FriendlyName,
					string.Equals(endpoint.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
			}

			return devices;
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			Log.Warning(exception, "Audio endpoints could not be enumerated.");
			return [];
		}
	}
}