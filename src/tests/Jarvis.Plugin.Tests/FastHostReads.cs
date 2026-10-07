using System;
using Jarvis.Plugin.Core;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Closes the host call gap for one fixture, for as long as that fixture runs.
/// <para>
/// The settings store spaces every read of the configuration by 150 ms, because the real host refuses a
/// plugin that calls back too quickly and a full reload is forty calls. A test host has no such limit, so the
/// handful of tests that reload the whole configuration were each paying six seconds of waiting and were
/// the slowest thing in the suite by an order of magnitude.
/// </para>
/// <para>
/// Only the wait changes. The order of the calls, the retry policy and the budget are the same code, so a
/// test that needs the real spacing can ask for it and a test that does not is exercising the same path
/// faster. The gap is restored on teardown whichever way the fixture ends, because it is process wide and a
/// fixture that left it closed would make a later test assert against a host that behaves unlike the real
/// one.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class FastHostReadsAttribute : TestFixtureAttribute
{
	[SetUp]
	public void CloseTheGap() => JarvisSettingsStore.SpaceHostCalls(TimeSpan.FromTicks(1));

	[TearDown]
	public void RestoreTheGap() =>
		JarvisSettingsStore.SpaceHostCalls(JarvisSettingsStore.DefaultHostCallSpacing);
}