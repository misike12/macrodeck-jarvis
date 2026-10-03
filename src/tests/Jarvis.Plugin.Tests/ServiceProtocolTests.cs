using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Jarvis.Service.Tests;

/// <summary>
/// The wire protocol and the guards around it.
/// <para>
/// This is the boundary between an assistant whose input is a model's output and a process that runs as
/// LocalSystem. Every test here is about what the service refuses, because the thing that must not happen
/// is a request being acted on because it was almost understood.
/// </para>
/// </summary>
[TestFixture]
public class ProtocolTests
{
	[Test]
	public void A_request_carries_the_version_and_the_operation()
	{
		var request = Protocol.Request("ping");

		Assert.Multiple(() =>
		{
			Assert.That(request["v"]!.GetValue<int>(), Is.EqualTo(Protocol.Version));
			Assert.That(request["op"]!.GetValue<string>(), Is.EqualTo("ping"));
			Assert.That(request["args"], Is.Not.Null);
		});
	}

	[Test]
	public void A_request_round_trips_through_text()
	{
		var line = Protocol.Request("registry_set", new JsonObject
		{
			["hive"] = "HKLM",
			["path"] = @"Software\Contoso",
			["name"] = "Setting",
			["value"] = "on",
		}).ToJsonString();

		var parsed = Protocol.ParseRequest(line);

		Assert.That(parsed, Is.Not.Null);
		Assert.That(parsed!["op"]!.GetValue<string>(), Is.EqualTo("registry_set"));
		Assert.That(parsed["args"]!["hive"]!.GetValue<string>(), Is.EqualTo("HKLM"));
	}

	/// <summary>
	/// The refusal that matters most. An unrecognised operation must never fall through to a default,
	/// because the default in a privileged process is "do nothing" at best and "do the nearest thing" at
	/// worst.
	/// </summary>
	[TestCase("drop_database")]
	[TestCase("")]
	[TestCase("PING")]
	[TestCase("ping ")]
	[TestCase("registry_set_all")]
	public void An_unknown_operation_is_refused(string operation)
	{
		var request = new JsonObject
		{
			["v"] = Protocol.Version,
			["op"] = operation,
			["args"] = new JsonObject(),
		};

		Assert.That(Protocol.ParseRequest(request.ToJsonString()), Is.Null, $"'{operation}' was accepted");
	}

	/// <summary>A version the service does not implement must be refused, not guessed at.</summary>
	[TestCase(0)]
	[TestCase(2)]
	[TestCase(99)]
	[TestCase(-1)]
	public void A_version_the_service_does_not_implement_is_refused(int version)
	{
		var request = new JsonObject
		{
			["v"] = version,
			["op"] = "ping",
			["args"] = new JsonObject(),
		};

		Assert.That(Protocol.ParseRequest(request.ToJsonString()), Is.Null);
	}

	[TestCase("")]
	[TestCase("   ")]
	[TestCase("not json")]
	[TestCase("{")]
	[TestCase("[1,2,3]")]
	[TestCase("\"just a string\"")]
	[TestCase("null")]
	public void Something_that_is_not_a_request_is_refused(string line)
	{
		Assert.That(Protocol.ParseRequest(line), Is.Null, $"'{line}' was accepted");
	}

	/// <summary>
	/// A request with no operation at all. The service would have nothing to do even if the rest parsed, and
	/// a null here is what would otherwise reach the switch.
	/// </summary>
	[Test]
	public void A_request_with_no_operation_is_refused()
	{
		var request = new JsonObject { ["v"] = Protocol.Version };

		Assert.That(Protocol.ParseRequest(request.ToJsonString()), Is.Null);
	}

	/// <summary>
	/// Bounded on purpose. A pipe is reachable by every process the user runs, and an unbounded line is a way
	/// to make the service allocate whatever a sender asked it to.
	/// </summary>
	[Test]
	public void An_oversized_request_is_refused()
	{
		var huge = new string('a', 128 * 1024);

		Assert.That(Protocol.ParseRequest(huge), Is.Null);
	}

	[Test]
	public void A_reply_says_whether_it_worked()
	{
		var ok = Protocol.Reply(true, "done");
		var failed = Protocol.Reply(false, "not done");

		Assert.Multiple(() =>
		{
			Assert.That(ok["ok"]!.GetValue<bool>(), Is.True);
			Assert.That(ok["content"]!.GetValue<string>(), Is.EqualTo("done"));
			Assert.That(failed["ok"]!.GetValue<bool>(), Is.False);
			Assert.That(failed["content"]!.GetValue<string>(), Is.EqualTo("not done"));
			Assert.That(failed["v"]!.GetValue<int>(), Is.EqualTo(Protocol.Version));
		});
	}

	/// <summary>
	/// The pipe name carries the user, because the pipe namespace is machine-wide. Without it, two users on
	/// one machine would share a service.
	/// </summary>
	[Test]
	public void The_pipe_name_carries_the_user()
	{
		Assert.Multiple(() =>
		{
			Assert.That(Protocol.PipeName, Does.Contain(Environment.UserName));
			Assert.That(Protocol.PipeName.Length, Is.LessThan(250), "a pipe name longer than 256 characters is rejected");
		});
	}

	[Test]
	public void Every_declared_operation_is_lowercase_and_underscored()
	{
		Assert.Multiple(() =>
		{
			foreach (var operation in Protocol.Operations)
			{
				Assert.That(operation, Is.EqualTo(operation.ToLowerInvariant()), operation);
				Assert.That(operation, Does.Not.Contain(" "), operation);
				Assert.That(operation, Does.Not.Contain("-"), operation);
			}
		});
	}

	[Test]
	public void The_declared_operations_are_unique()
	{
		Assert.That(Protocol.Operations.Distinct().Count(), Is.EqualTo(Protocol.Operations.Length));
	}
}
