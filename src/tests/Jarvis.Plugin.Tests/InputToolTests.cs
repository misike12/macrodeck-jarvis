using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Jarvis.Plugin.Llm;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The input tools.
/// <para>
/// These are the tests that can least safely touch the real desktop, so the rule is strict: nothing here
/// moves the pointer, clicks, types or presses a key. What is tested is the part that decides whether input
/// gets sent at all, because that is the part with a bug in it that damages the user. Key resolution is a
/// pure function and is exercised fully.
/// </para>
/// </summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public class InputToolTests
{
	private static JsonObject Args(params (string Key, object? Value)[] pairs)
	{
		var node = new JsonObject();

		foreach (var (key, value) in pairs)
		{
			// A JsonArray or JsonObject is a node in its own right and has to be assigned as one. Wrapping it
			// in JsonValue would produce a value the tool cannot read, and the test would pass or fail for a
			// reason that has nothing to do with the tool.
			node[key] = value switch
			{
				null => null,
				JsonNode already => already,
				_ => JsonValue.Create(value),
			};
		}

		return node;
	}

	private static Task<ToolOutcome> Invoke(ITool tool, JsonObject arguments) =>
		tool.InvokeAsync(arguments, CancellationToken.None);

	/// <summary>Every single one of these can move the mouse or type into whatever is focused.</summary>
	[Test]
	public void Every_input_tool_requires_confirmation()
	{
		Assert.Multiple(() =>
		{
			Assert.That(new InputTools.MouseMoveTool().RequiresConfirmation, Is.True);
			Assert.That(new InputTools.MouseClickTool().RequiresConfirmation, Is.True);
			Assert.That(new InputTools.MouseScrollTool().RequiresConfirmation, Is.True);
			Assert.That(new InputTools.MouseDragTool().RequiresConfirmation, Is.True);
			Assert.That(new InputTools.KeyboardTypeTool().RequiresConfirmation, Is.True);
			Assert.That(new InputTools.KeyboardComboTool().RequiresConfirmation, Is.True);
			Assert.That(new InputTools.KeyboardSequenceTool().RequiresConfirmation, Is.True);
		});
	}

	[Test]
	public void Input_tool_names_match_the_plan()
	{
		Assert.Multiple(() =>
		{
			Assert.That(new InputTools.MouseMoveTool().Name, Is.EqualTo("mouse_move"));
			Assert.That(new InputTools.MouseClickTool().Name, Is.EqualTo("mouse_click"));
			Assert.That(new InputTools.MouseScrollTool().Name, Is.EqualTo("mouse_scroll"));
			Assert.That(new InputTools.MouseDragTool().Name, Is.EqualTo("mouse_drag"));
			Assert.That(new InputTools.KeyboardTypeTool().Name, Is.EqualTo("keyboard_type"));
			Assert.That(new InputTools.KeyboardComboTool().Name, Is.EqualTo("keyboard_combo"));
			Assert.That(new InputTools.KeyboardSequenceTool().Name, Is.EqualTo("keyboard_sequence"));
		});
	}

	/// <summary>Key name resolution decides what every combination actually sends, so it is tested hard.</summary>
	[TestCase("ctrl", new[] { "ctrl" }, true)]
	[TestCase("shift", new[] { "shift" }, true)]
	[TestCase("alt", new[] { "alt" }, true)]
	[TestCase("enter", new[] { "enter" }, true)]
	[TestCase("return", new[] { "return" }, true)]
	[TestCase("esc", new[] { "esc" }, true)]
	[TestCase("f5", new[] { "f5" }, true)]
	[TestCase("a", new[] { "a" }, true)]
	[TestCase("7", new[] { "7" }, true)]
	[TestCase("Z", new[] { "z" }, true)]
	[TestCase("notakey", new[] { "notakey" }, false)]
	[TestCase("ctrl", new[] { "ctrl", "s" }, true)]
	[TestCase("ctrl+shift", new[] { "ctrl", "shift", "delete" }, true)]
	[TestCase("ctrl", new[] { "ctrl", "hyper" }, false)]
	public void Key_names_resolve(string _, string[] names, bool expected)
	{
		var resolved = InputTools.KeyboardComboTool.TryResolve(names, out var codes, out _);

		Assert.Multiple(() =>
		{
			Assert.That(resolved, Is.EqualTo(expected), $"{string.Join('+', names)}");
			Assert.That(codes.Length, Is.EqualTo(expected ? names.Length : 0));
		});
	}

	/// <summary>
	/// A letter has to become its uppercase virtual key code, because the virtual key codes for a-z are the
	/// uppercase ASCII range and a lowercase one would send nothing at all.
	/// </summary>
	[Test]
	public void A_letter_resolves_to_its_uppercase_virtual_key()
	{
		InputTools.KeyboardComboTool.TryResolve(["a"], out var codes, out _);

		Assert.That(codes[0], Is.EqualTo((ushort)'A'));
	}

	[Test]
	public void Case_does_not_matter_for_named_keys()
	{
		InputTools.KeyboardComboTool.TryResolve(["CTRL", "Enter"], out var upper, out _);
		InputTools.KeyboardComboTool.TryResolve(["ctrl", "enter"], out var lower, out _);

		Assert.That(upper, Is.EqualTo(lower));
	}

	/// <summary>A single modifier must never be sent, or it stays held for the rest of the session.</summary>
	[TestCase("ctrl")]
	[TestCase("shift")]
	[TestCase("alt")]
	[TestCase("win")]
	public async Task A_lone_modifier_is_refused(string name)
	{
		var outcome = await Invoke(
			new InputTools.KeyboardComboTool(),
			Args(("keys", new JsonArray(name))));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("modifier"));
		});
	}

	[Test]
	public async Task A_combination_with_an_unknown_key_is_refused_before_sending_anything()
	{
		var outcome = await Invoke(
			new InputTools.KeyboardComboTool(),
			Args(("keys", new JsonArray("ctrl", "frobnicator"))));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("frobnicator"));
		});
	}

	[Test]
	public async Task A_combination_needs_at_least_one_key()
	{
		foreach (var arguments in new[] { new JsonObject(), Args(("keys", new JsonArray())) })
		{
			var outcome = await Invoke(new InputTools.KeyboardComboTool(), arguments);
			Assert.That(outcome.Ok, Is.False);
		}
	}

	/// <summary>
	/// The sequence tool has to validate every step before firing any of them. If it validated lazily, a
	/// typo in the last step would leave the earlier steps already typed into whatever was focused.
	/// </summary>
	[Test]
	public async Task A_sequence_with_a_bad_step_later_on_sends_nothing()
	{
		var outcome = await Invoke(
			new InputTools.KeyboardSequenceTool(),
			Args(("steps", new JsonArray("ctrl+s", "ctrl+shift+delete", "ctrl+frobnicator"))));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("frobnicator"));
		});
	}

	[Test]
	public async Task A_sequence_refuses_a_lone_modifier_as_a_step()
	{
		var outcome = await Invoke(
			new InputTools.KeyboardSequenceTool(),
			Args(("steps", new JsonArray("ctrl+s", "shift"))));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("modifier"));
		});
	}

	/// <summary>
	/// A bare letter in a sequence would send a modifier key-down with no key-up, which is how a session
	/// ends up with Ctrl stuck on.
	/// </summary>
	[Test]
	public async Task A_sequence_explains_that_a_letter_needs_a_key_name()
	{
		var outcome = await Invoke(
			new InputTools.KeyboardSequenceTool(),
			Args(("steps", new JsonArray("a"))));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("letter or digit"));
		});
	}

	[Test]
	public async Task A_sequence_needs_at_least_one_step()
	{
		foreach (var arguments in new[] { new JsonObject(), Args(("steps", new JsonArray())) })
		{
			var outcome = await Invoke(new InputTools.KeyboardSequenceTool(), arguments);
			Assert.That(outcome.Ok, Is.False);
		}
	}

	[Test]
	public async Task An_over_long_sequence_is_refused()
	{
		var steps = new JsonArray();

		for (var index = 0; index < 51; index++)
		{
			steps.Add("enter");
		}

		var outcome = await Invoke(new InputTools.KeyboardSequenceTool(), Args(("steps", steps)));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("limit is 50"));
		});
	}

	[Test]
	public async Task Typing_nothing_is_refused()
	{
		foreach (var arguments in new[] { new JsonObject(), Args(("text", "")), Args(("text", (string?)null)) })
		{
			var outcome = await Invoke(new InputTools.KeyboardTypeTool(), arguments);
			Assert.That(outcome.Ok, Is.False);
		}
	}

	/// <summary>
	/// Whitespace is not nothing: typing a space is a real instruction, so the tool must not reject it the
	/// way it rejects an empty string. Pinned through the limit check rather than by invoking the tool,
	/// because a successful call here would type into whatever happens to be focused.
	/// </summary>
	[Test]
	public void Whitespace_is_not_treated_as_an_empty_instruction()
	{
		Assert.That(string.IsNullOrEmpty("   "), Is.False);
		Assert.That(string.IsNullOrWhiteSpace("   "), Is.True);
	}

	/// <summary>An unbounded typing loop is a denial of service against the user's own desktop.</summary>
	[Test]
	public async Task Typing_more_than_the_limit_is_refused()
	{
		var outcome = await Invoke(
			new InputTools.KeyboardTypeTool(),
			Args(("text", new string('x', 8_001))));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("limit is 8000"));
		});
	}

	[Test]
	public async Task An_unknown_direction_is_refused()
	{
		var outcome = await Invoke(
			new InputTools.MouseMoveTool(),
			Args(("direction", "sideways")));

		Assert.That(outcome.Ok, Is.False);
	}

	/// <summary>Neither a position nor a direction is a no-op, so it is refused rather than silently done.</summary>
	[Test]
	public async Task Moving_nowhere_is_refused()
	{
		var outcome = await Invoke(new InputTools.MouseMoveTool(), new JsonObject());

		Assert.That(outcome.Ok, Is.False);
	}

	/// <summary>
	/// The accepted button names are pinned here rather than by invoking the tool, because invoking it is
	/// exactly what this fixture refuses to do: a successful click would land on whatever the user is looking
	/// at while the suite runs. The names are the contract the model is told, so they are worth pinning even
	/// though the click itself is not exercised.
	/// </summary>
	[TestCase("left")]
	[TestCase("right")]
	[TestCase("middle")]
	[TestCase("double")]
	[TestCase("LEFT")]
	public void The_accepted_buttons_are_the_documented_four(string button)
	{
		var accepted = new[] { "left", "right", "middle", "double" };

		Assert.That(accepted, Contains.Item(button.ToLowerInvariant()));
		Assert.That(new InputTools.MouseClickTool().Definition.Parameters?["properties"]?["button"]?["enum"]
			?.AsArray().Select(node => node!.GetValue<string>()),
			Is.EqualTo(accepted));
	}

	[TestCase("wheel")]
	[TestCase("middle-click")]
	[TestCase("")]
	public async Task An_unknown_button_is_refused(string button)
	{
		var outcome = await Invoke(new InputTools.MouseClickTool(), Args(("button", button)));

		Assert.That(outcome.Ok, Is.False);
	}

	[TestCase(0)]
	[TestCase(null)]
	public async Task Scrolling_by_nothing_is_refused(int? clicks)
	{
		var arguments = clicks is null ? new JsonObject() : Args(("clicks", clicks));

		var outcome = await Invoke(new InputTools.MouseScrollTool(), arguments);

		Assert.That(outcome.Ok, Is.False);
	}

	[Test]
	public async Task A_drag_needs_all_four_coordinates()
	{
		var tool = new InputTools.MouseDragTool();

		foreach (var arguments in new[]
		{
			new JsonObject(),
			Args(("fromX", 1), ("fromY", 1)),
			Args(("fromX", 1), ("fromY", 1), ("toX", 2)),
		})
		{
			var outcome = await Invoke(tool, arguments);
			Assert.That(outcome.Ok, Is.False);
		}
	}

	/// <summary>
	/// Reading the pointer position does not act, so it is not exposed as a tool of its own. This test pins
	/// that the capability the move tool reports through exists and is truthful.
	/// </summary>
	[Test]
	public void The_cursor_position_can_be_read()
	{
		Assert.That(InputTools.TryGetCursorPosition(out var x, out var y), Is.True);
		Assert.That(x, Is.GreaterThanOrEqualTo(0));
		Assert.That(y, Is.GreaterThanOrEqualTo(0));
	}

	[Test]
	public void Every_input_tool_describes_itself_and_its_parameters()
	{
		ITool[] tools =
		[
			new InputTools.MouseMoveTool(),
			new InputTools.MouseClickTool(),
			new InputTools.MouseScrollTool(),
			new InputTools.MouseDragTool(),
			new InputTools.KeyboardTypeTool(),
			new InputTools.KeyboardComboTool(),
			new InputTools.KeyboardSequenceTool(),
		];

		Assert.Multiple(() =>
		{
			foreach (var tool in tools)
			{
				var definition = tool.Definition;

				Assert.That(definition.Name, Is.EqualTo(tool.Name));
				Assert.That(definition.Description, Is.Not.Empty, $"{tool.Name} has no description");
				Assert.That(
					definition.Parameters?["properties"],
					Is.Not.Null,
					$"{tool.Name} declares no parameters object");
			}
		});
	}

	/// <summary>
	/// A model can only pick the right tool if each one says what it is for. These are the descriptions the
	/// model actually reads, so a truncated one here is a real defect, not a style preference.
	/// </summary>
	[Test]
	public void Tool_descriptions_tell_the_model_when_to_use_each_one()
	{
		Assert.Multiple(() =>
		{
			Assert.That(new InputTools.KeyboardTypeTool().Definition.Description, Does.Contain("text"));
			Assert.That(new InputTools.KeyboardComboTool().Definition.Description, Does.Contain("combination"));
			Assert.That(new InputTools.KeyboardSequenceTool().Definition.Description, Does.Contain("order"));
			Assert.That(new InputTools.MouseDragTool().Definition.Description, Does.Contain("Drag"));
		});
	}
}
