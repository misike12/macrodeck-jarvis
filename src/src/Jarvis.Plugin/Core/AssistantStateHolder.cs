using System.Collections.Immutable;

namespace Jarvis.Plugin.Core;

/// <summary>
/// The single place a state change is published. The session owns the current value and raises
/// <see cref="Changed"/>; readers take snapshots so a widget never sees a torn set of fields.
/// </summary>
public sealed record AssistantSnapshot
{
	public required AssistantState State { get; init; }

	public required string StatusLine { get; init; }

	public required string Transcript { get; init; }

	public required string Reply { get; init; }

	public double Amplitude { get; init; }

	public DateTimeOffset ChangedAt { get; init; }

	public bool IsBusy => State is not AssistantState.Idle and not AssistantState.Unavailable;

	public static AssistantSnapshot Idle { get; } = new()
	{
		State = AssistantState.Idle,
		StatusLine = string.Empty,
		Transcript = string.Empty,
		Reply = string.Empty,
		ChangedAt = DateTimeOffset.MinValue,
	};
}

/// <summary>
/// Holds JARVIS's observable state. Transitions are serialized on a single gate so a widget can
/// never observe a half-applied update, and subscribers are notified after the gate is released so
/// a slow reader cannot stall a state transition.
/// </summary>
public sealed class AssistantStateHolder
{
	private readonly Lock _gate = new();
	private AssistantSnapshot _current = AssistantSnapshot.Idle;
	private ImmutableArray<Action<AssistantSnapshot>> _subscribers = [];

	public event Action<AssistantSnapshot>? Changed;

	public AssistantSnapshot Current
	{
		get
		{
			lock (_gate)
			{
				return _current;
			}
		}
	}

	public IDisposable Subscribe(Action<AssistantSnapshot> observer)
	{
		lock (_gate)
		{
			_subscribers = _subscribers.Add(observer);
			observer(_current);
		}

		return new Subscription(this, observer);
	}

	public void Transition(
		AssistantState state,
		string? statusLine = null,
		string? transcript = null,
		string? reply = null,
		double? amplitude = null)
	{
		AssistantSnapshot next;
		Action<AssistantSnapshot>[] subscribers;

		lock (_gate)
		{
			next = _current with
			{
				State = state,
				StatusLine = statusLine ?? _current.StatusLine,
				Transcript = transcript ?? _current.Transcript,
				Reply = reply ?? _current.Reply,
				Amplitude = amplitude ?? _current.Amplitude,
				ChangedAt = DateTimeOffset.UtcNow,
			};

			if (next == _current)
			{
				return;
			}

			_current = next;
			subscribers = _subscribers.IsEmpty ? [] : _subscribers.ToArray();
		}

		foreach (var subscriber in subscribers)
		{
			subscriber(next);
		}

		Changed?.Invoke(next);
	}

	/// <summary>
	/// Amplitude arrives far faster than any other change and must never allocate a notification per
	/// sample, so it is folded into the current snapshot and only emitted once per interval.
	/// </summary>
	public void UpdateAmplitude(double amplitude)
	{
		Action<AssistantSnapshot>[] subscribers;

		lock (_gate)
		{
			if (Math.Abs(_current.Amplitude - amplitude) < AmplitudeDeadband)
			{
				return;
			}

			_current = _current with { Amplitude = amplitude, ChangedAt = DateTimeOffset.UtcNow };
			subscribers = _subscribers.IsEmpty ? [] : _subscribers.ToArray();
		}

		foreach (var subscriber in subscribers)
		{
			subscriber(_current);
		}
	}

	public void Reset()
	{
		Action<AssistantSnapshot>[] subscribers;

		lock (_gate)
		{
			if (_current == AssistantSnapshot.Idle)
			{
				return;
			}

			_current = AssistantSnapshot.Idle;
			subscribers = _subscribers.IsEmpty ? [] : _subscribers.ToArray();
		}

		foreach (var subscriber in subscribers)
		{
			subscriber(_current);
		}

		Changed?.Invoke(_current);
	}

	private const double AmplitudeDeadband = 0.04;

	private void Unsubscribe(Action<AssistantSnapshot> observer)
	{
		lock (_gate)
		{
			_subscribers = _subscribers.Remove(observer);
		}
	}

	private sealed class Subscription(AssistantStateHolder owner, Action<AssistantSnapshot> observer) : IDisposable
	{
		private Action<AssistantSnapshot>? _observer = observer;

		public void Dispose()
		{
			var target = Interlocked.Exchange(ref _observer, null);
			if (target is not null)
			{
				owner.Unsubscribe(target);
			}
		}
	}
}