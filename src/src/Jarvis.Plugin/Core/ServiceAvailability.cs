using Serilog;

namespace Jarvis.Plugin.Core;

/// <summary>
/// Whether the elevated service is actually there, cached for a short while.
/// <para>
/// The answer changes without the plugin hearing about it: the service can be stopped, reinstalled or
/// uninstalled from outside. Probing on every tool call would put a pipe round trip in front of each one,
/// and not probing at all is how a user ends up with a tool that silently does nothing. So the answer is
/// probed on a timer and re-probed whenever it is older than the cache.
/// </para>
/// <para>
/// The cache is what makes this safe to read from a capability handler, since it never awaits.
/// </para>
/// </summary>
public sealed class ServiceAvailability(ElevatedServiceClient client, ILogger logger)
{
	/// <summary>
	/// How long an answer is trusted. Short enough that starting the service is noticed within a turn or
	/// two, long enough that a burst of tool calls does not become a burst of pipe connections.
	/// </summary>
	private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(20);

	private readonly Lock _gate = new();
	private readonly ElevatedServiceClient _client = client;
	private readonly ILogger _logger = logger.ForContext<ServiceAvailability>();

	private DateTimeOffset _probedAt = DateTimeOffset.MinValue;
	private bool _lastAnswer;

	/// <summary>
	/// The most recent answer, without waiting. False until the first probe completes, which is the safe
	/// direction: offering the tool and then refusing is recoverable, hiding it and never showing it is not.
	/// </summary>
	public bool IsAvailable
	{
		get
		{
			lock (_gate)
			{
				return _lastAnswer && DateTimeOffset.UtcNow - _probedAt < CacheLifetime;
			}
		}
	}

	/// <summary>
	/// Probes the service and remembers the answer. Called at initialization and whenever a caller wants a
	/// fresher answer than the cache holds.
	/// </summary>
	public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
	{
		var answer = await _client.IsAvailableAsync(cancellationToken).ConfigureAwait(false);

		lock (_gate)
		{
			if (answer != _lastAnswer)
			{
				_logger.Information(
					"The elevated service is now {State}.", answer ? "answering" : "not answering");
			}

			_lastAnswer = answer;
			_probedAt = DateTimeOffset.UtcNow;
		}

		return answer;
	}
}