using System.Text.Json.Nodes;
using Jarvis.Plugin.Vision;
using Serilog;

namespace Jarvis.Plugin.Llm;

/// <summary>
/// Lets the model look at the screen when it decides that is the way to answer.
/// <para>
/// Requires confirmation. A screenshot is a picture of everything on the display, including whatever is in
/// another window and anything the user did not intend to share, and it leaves the machine. A tool that
/// sends the screen to a service is exactly the kind of thing a user should be able to see and refuse
/// before it happens rather than discover afterwards.
/// </para>
/// </summary>
public sealed class ScreenshotTool(VisionClient vision, ILogger logger) : ITool
{
	public string Name => "screenshot";

	public bool RequiresConfirmation => true;

	public ToolDefinition Definition => new()
	{
		Name = Name,
		Description = "Photographs the screen and describes what is on it. Use this when the user asks what "
			+ "they are looking at, asks about a window, or asks to read something on the screen.",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["question"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "What to look for on the screen, in the user's own words.",
				},
				["allScreens"] = new JsonObject
				{
					["type"] = "boolean",
					["description"] = "Capture every monitor instead of only the primary one. Default false.",
				},
			},
		},
	};

	public async Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
	{
		var question = arguments["question"]?.GetValue<string>()
			?? "Describe what is on this screen.";

		var target = arguments["allScreens"]?.GetValue<bool>() == true
			? CaptureTarget.AllScreens
			: CaptureTarget.PrimaryScreen;

		try
		{
			var capture = ScreenCaptureService.Capture(target, logger);
			var result = await vision.DescribeScreenAsync(capture, question, cancellationToken).ConfigureAwait(false);

			return result.Ok
				? ToolOutcome.Success(result.Text)
				: ToolOutcome.Failure($"The screen could not be described: {result.Failure} {result.Detail}");
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			logger.Warning(exception, "A screenshot could not be taken.");
			return ToolOutcome.Failure($"The screen could not be captured: {exception.Message}");
		}
	}
}