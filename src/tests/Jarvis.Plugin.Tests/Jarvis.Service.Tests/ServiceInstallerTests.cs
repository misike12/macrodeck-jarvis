using NUnit.Framework;

namespace Jarvis.Service.Tests;

/// <summary>
/// The installer's refusal decision.
/// <para>
/// This exists because of a specific bug: <c>is { } denial</c> matches an empty string as well as a real
/// one, and the refusal helper returns an empty string precisely when the process *is* elevated. The result
/// was that the install refused every time, including from an elevated shell, and printed nothing, because
/// the reason it had was the empty string. An exit code with no explanation and no way to get a success.
/// </para>
/// </summary>
[TestFixture]
public class ServiceInstallerTests
{
	/// <summary>
	/// The regression itself. An elevated shell must produce no refusal at all, and a test that reads
	/// differently here is the only thing that catches it.
	/// </summary>
	[Test]
	public void An_elevated_shell_is_never_refused()
	{
		Assert.That(ServiceInstaller.RefusalFor(isAdministrator: true, "Installing the service"), Is.Empty);
	}

	[Test]
	public void An_unelevated_shell_is_refused_with_a_reason()
	{
		var refusal = ServiceInstaller.RefusalFor(isAdministrator: false, "Installing the service");

		Assert.Multiple(() =>
		{
			Assert.That(refusal, Is.Not.Empty);
			Assert.That(refusal, Does.Contain("administrator"));
			Assert.That(refusal, Does.Contain("Installing the service"), "the reason must name what failed");
		});
	}

	/// <summary>
	/// The empty string is the success signal, so it has to be distinguishable from a refusal. This is the
	/// property the pattern above depends on.
	/// </summary>
	[Test]
	public void Success_and_refusal_are_distinguishable()
	{
		var refusal = ServiceInstaller.RefusalFor(isAdministrator: false, "x");

		Assert.Multiple(() =>
		{
			Assert.That(refusal is { Length: > 0 }, Is.True);
			Assert.That(ServiceInstaller.RefusalFor(isAdministrator: true, "x") is { Length: > 0 }, Is.False);
		});
	}

	/// <summary>Both install and uninstall go through the same decision, so both are covered by construction.</summary>
	[TestCase("Installing the service")]
	[TestCase("Removing the service")]
	public void The_reason_names_the_action(string action)
	{
		Assert.That(ServiceInstaller.RefusalFor(isAdministrator: false, action), Does.Contain(action));
	}
}
