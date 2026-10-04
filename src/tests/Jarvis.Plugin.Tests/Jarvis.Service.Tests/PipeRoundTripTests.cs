using System.Text.Json.Nodes;
using Jarvis.Plugin.Core;
using NUnit.Framework;

namespace Jarvis.Service.Tests;

/// <summary>
/// The pipe, end to end: a real service listener and the plugin's real client, over a real named pipe.
/// <para>
/// This is the only test that proves the two halves agree. Every protocol test on either side can pass
/// while the pipe name, the framing or the direction of the conversation differs between them, and that is
/// exactly the failure that makes the elevated tools appear to do nothing at all.
/// </para>
/// <para>
/// It is also the test that caught the byte order mark. <c>Encoding.UTF8</c> writes a preamble, and on a
/// duplex pipe that preamble deadlocked against the other end's first write; where it did not, it prefixed
/// every request with three bytes that are not JSON. Nothing above this layer could have shown either.
/// </para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class PipeRoundTripTests
{
	private ElevatedServiceClient _client = null!;
	private PipeServer _server = null!;
	private string _pipeName = null!;

	[SetUp]
	public void SetUp()
	{
		// A pipe name of its own per test. The service allows unlimited instances on one name, so a
		// listener left behind by an earlier test would otherwise accept this test's connection and answer
		// from a pipe nobody is reading.
		_pipeName = Protocol.PipeNameForSuffix(UniqueSuffix());

		// Short, so a broken pipe fails the test rather than stalling the suite.
		_client = new ElevatedServiceClient(TimeSpan.FromSeconds(10), _pipeName);
		_server = new PipeServer(NullLogger.Instance, _pipeName);
		_server.Start();
	}

	[TearDown]
	public void TearDown() => _server.Dispose();

	/// <summary>
	/// The pipe name is built in two projects that do not reference each other, so this is what catches a
	/// change to one and not the other.
	/// </summary>
	[Test]
	public void Both_sides_name_the_same_pipe()
	{
		Assert.That(
			ElevatedServiceClient.ServicePipeName,
			Is.EqualTo(Protocol.PipeName),
			"the plugin and the service disagree about the pipe name");
	}

	[Test]
	public async Task A_ping_is_answered_over_the_pipe()
	{
		var reply = await _client.SendAsync(ServiceProtocol.Request("ping"), CancellationToken.None);

		var (ok, content) = ServiceProtocol.Read(reply);

		Assert.Multiple(() =>
		{
			Assert.That(reply, Is.Not.Null, "no reply came back");
			Assert.That(ok, Is.True, content);
			Assert.That(content, Is.EqualTo("pong"));
		});
	}

	[Test]
	public async Task The_service_is_reported_as_available_when_it_is_listening()
	{
		Assert.That(await _client.IsAvailableAsync(CancellationToken.None), Is.True);
	}

	/// <summary>
	/// The first request on a connection must arrive clean. A byte order mark on it would make every call
	/// fail, so this is asserted separately from the ping succeeding later in the connection.
	/// </summary>
	[Test]
	public async Task The_very_first_request_is_not_prefixed_with_anything()
	{
		var reply = await _client.SendAsync(ServiceProtocol.Request("ping"), CancellationToken.None);

		Assert.That(
			reply?["content"]?.GetValue<string>(),
			Is.EqualTo("pong"),
			"the first request on a fresh connection did not arrive intact");
	}

	/// <summary>
	/// A service that is not running is an ordinary state, not a fault. The client reports it as
	/// unavailable rather than throwing, because the caller falls back to what the plugin can do itself.
	/// <para>
	/// The listener is stopped first, because with the fixture's own service still running there would
	/// simply be something to connect to, and this would be testing the wrong thing entirely.
	/// </para>
	/// </summary>
	[Test]
	public async Task No_service_means_unavailable_rather_than_an_exception()
	{
		_server.Dispose();

		var orphan = new ElevatedServiceClient(TimeSpan.FromSeconds(2), Protocol.PipeNameForSuffix(UniqueSuffix()));

		Assert.That(await orphan.IsAvailableAsync(CancellationToken.None), Is.False);
	}

	[Test]
	public async Task An_unanswered_request_is_reported_rather_than_waiting_forever()
	{
		_server.Dispose();

		var reply = await _client.SendAsync(ServiceProtocol.Request("ping"), CancellationToken.None);

		var (ok, content) = ServiceProtocol.Read(reply);

		Assert.Multiple(() =>
		{
			Assert.That(ok, Is.False);
			Assert.That(content, Does.Contain("did not answer"));
		});
	}

	[Test]
	public async Task A_registry_read_goes_all_the_way_through()
	{
		var reply = await _client.SendAsync(
			ServiceProtocol.Request("registry_get", new JsonObject
			{
				["hive"] = "HKCU",
				["path"] = @"Software\JarvisPipeTests\Nothing",
				["name"] = "Nothing",
			}),
			CancellationToken.None);

		var (ok, content) = ServiceProtocol.Read(reply);

		Assert.Multiple(() =>
		{
			Assert.That(reply, Is.Not.Null);
			Assert.That(ok, Is.False, "a key that does not exist should not read as success");
			Assert.That(content, Does.Contain("does not exist"));
		});
	}

	/// <summary>
	/// The refusal has to survive the round trip with its reason intact, because that reason is the only
	/// thing that tells a model it should stop rather than try again.
	/// </summary>
	[Test]
	public async Task A_refusal_keeps_its_reason_across_the_pipe()
	{
		var reply = await _client.SendAsync(
			ServiceProtocol.Request("registry_get", new JsonObject
			{
				["hive"] = "HKLM",
				["path"] = @"\SYSTEM\CurrentControlSet\Services\Schedule",
			}),
			CancellationToken.None);

		var (ok, content) = ServiceProtocol.Read(reply);

		Assert.Multiple(() =>
		{
			Assert.That(ok, Is.False);
			Assert.That(content, Does.Contain("not allowed"));
		});
	}

	/// <summary>
	/// One connection carries several requests. The client opens a pipe per call, so this also proves a
	/// closed connection is not mistaken for a broken service.
	/// </summary>
	[Test]
	public async Task Several_requests_all_succeed()
	{
		for (var attempt = 0; attempt < 3; attempt++)
		{
			var reply = await _client.SendAsync(ServiceProtocol.Request("ping"), CancellationToken.None);

			Assert.That(ServiceProtocol.Read(reply).Ok, Is.True, $"request {attempt} failed");
		}
	}

	/// <summary>A request carrying arguments must keep them intact through the framing.</summary>
	[Test]
	public async Task Arguments_survive_the_round_trip()
	{
		var reply = await _client.SendAsync(
			ServiceProtocol.Request("registry_get", new JsonObject
			{
				["hive"] = "HKCU",
				["path"] = @"Software\JarvisPipeTests",
				["name"] = "Absent",
			}),
			CancellationToken.None);

		var (ok, content) = ServiceProtocol.Read(reply);

		Assert.Multiple(() =>
		{
			Assert.That(ok, Is.False);
			Assert.That(content, Does.Contain("JarvisPipeTests"), "the path did not arrive intact");
		});
	}

	/// <summary>
	/// A pipe-name suffix that is actually unique.
	/// <para>
	/// The first eight hex characters of a version 7 GUID are the high bits of its timestamp, so two
	/// identifiers minted in the same millisecond share them. They looked unique and were not, which only
	/// stayed hidden because the service allowed unlimited instances per pipe name: every test in a fixture
	/// was quietly talking to the first server the fixture started.
	/// </para>
	/// </summary>
	private static string UniqueSuffix() => Guid.CreateVersion7().ToString("N")[^8..];
}
