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

	public static bool TryOpenCapture(AudioDevice device, out WasapiRecorder? recorder, out string? error)
	{
		recorder = null;
		error = null;

		try
		{
			using var enumerator = new MMDeviceEnumerator();
			var endpoint = enumerator.GetDevice(device.Id);

			// Shared mode with automatic format conversion: the endpoint's native mix format is usually
			// not float32, and asking for a specific format is what makes capture fail on odd hardware.
			recorder = new WasapiRecorderBuilder()
				.WithDevice(endpoint)
				.WithSharedMode()
				.WithPollingSync()
				.WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1))
				.Build();
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			error = exception.Message;
			recorder?.Dispose();
			recorder = null;
			return false;
		}

		return true;
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