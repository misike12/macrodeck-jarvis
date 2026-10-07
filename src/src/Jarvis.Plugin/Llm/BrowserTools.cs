using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;

namespace Jarvis.Plugin.Llm;

/// <summary>
/// Browser control, driving a Chromium browser over the DevTools Protocol.
/// <para>
/// The browser is launched with its own profile directory rather than attaching to the one already running.
/// That means the browser this drives is a separate instance with its own cookies and its own windows, so
/// automating a site cannot log the user out of it or move the window they are working in. The profile
/// directory lives under the plugin's data directory and persists between runs, so a login only has to
/// happen once.
/// </para>
/// <para>
/// The protocol is spoken over a WebSocket rather than through a browser-automation package, because the
/// package would be a large dependency for what is a handful of documented message types.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class BrowserTools
{
	/// <summary>
	/// One browser per process, shared by every tool. Launching a second one would race the first for the
	/// profile directory lock and the debugging port.
	/// </summary>
	private static readonly SemaphoreSlim Gate = new(1, 1);

	private static ThBrowser? _browser;
	private static string? _profileDirectory;

	/// <summary>
	/// The port the browser is asked to open. A fixed port is deliberate: the browser is found through the
	/// address it publishes rather than by guessing, and a fixed port means the address is predictable.
	/// </summary>
	private const int DebuggingPort = 9333;

	/// <summary>How long to wait for the browser to publish its debugging port.</summary>
	private static TimeSpan StartupTimeout => TimeSpan.FromSeconds(10);

	private const string DefaultExecutable =
		@"C:\Users\Misu\AppData\Local\Thorium\Application\thorium.exe";

	/// <summary>
	/// Where the browser to drive is looked for, in order. Chromium browsers all take the same debugging
	/// flags, so any of them will do; the user's own choice is first.
	/// </summary>
	private static readonly string[] CandidateExecutables =
	[
		@"C:\Users\Misu\AppData\Local\Thorium\Application\thorium.exe",
		@"C:\Program Files\Google\Chrome\Application\chrome.exe",
		@"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
		@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
		@"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
	];

	/// <summary>
	/// Launches the browser if it is not already running, and returns a session that talks to it.
	/// </summary>
	private static async Task<(ThBrowser? Browser, string? Error)> ConnectAsync(CancellationToken cancellationToken)
	{
		if (_browser is { IsConnected: true })
		{
			return (_browser, null);
		}

		await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			if (_browser is { IsConnected: true })
			{
				return (_browser, null);
			}

			_browser?.Dispose();
			_browser = null;

			// The port is probed first. A browser that is already listening on it is reused rather than
			// launched a second time, which is what happens after the plugin restarts mid-session.
			var version = await ThBrowser.ReadVersionAsync(DebuggingPort, cancellationToken).ConfigureAwait(false);

			if (version is null)
			{
				var executable = CandidateExecutables.FirstOrDefault(File.Exists);

				if (executable is null)
				{
					return (null,
						"No Chromium browser was found. Set the path to one in the settings.");
				}

				_ = executable;

				if (!await LaunchAsync(cancellationToken).ConfigureAwait(false))
				{
					return (null, "The browser could not be started.");
				}

				// Bounded well below the host's thirty second ceiling, because this wait is followed by connecting to
				// the browser, which has a cost of its own. A thirty second poll followed by a five second
				// connect is a press the host has already given up on.
				using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

				startup.CancelAfter(StartupTimeout);

				var started = DateTime.UtcNow;

				try
				{
					while (DateTime.UtcNow - started < StartupTimeout)
					{
						version = await ThBrowser.ReadVersionAsync(DebuggingPort, startup.Token).ConfigureAwait(false);

						if (version is not null)
						{
							break;
						}

						await Task.Delay(250, startup.Token).ConfigureAwait(false);
					}
				}
				catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
				{
					return (null, $"The browser did not offer its debugging port within {StartupTimeout.TotalSeconds:0} seconds.");
				}

				if (version is null)
				{
					return (null, $"The browser did not offer its debugging port within {StartupTimeout.TotalSeconds:0} seconds.");
				}
			}

			_browser = await ThBrowser.CreateAsync(DebuggingPort, cancellationToken).ConfigureAwait(false);
			return (_browser, null);
		}
		finally
		{
			Gate.Release();
		}
	}

	private static string ProfileDirectory =>
		_profileDirectory ??= Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"JarvisPlugin",
			"BrowserProfile");

	private static async Task<bool> LaunchAsync(CancellationToken cancellationToken)
	{
		var executable = CandidateExecutables.FirstOrDefault(File.Exists);

		if (executable is null)
		{
			return false;
		}

		Directory.CreateDirectory(ProfileDirectory);

		var startInfo = new ProcessStartInfo
		{
			FileName = executable,
			UseShellExecute = false,
		};

		// A dedicated profile, so the automation cannot touch the user's real session, and a port, so the
		// plugin can find it again after a restart.
		startInfo.ArgumentList.Add("--remote-debugging-port=" + DebuggingPort);
		startInfo.ArgumentList.Add("--user-data-dir=" + ProfileDirectory);
		startInfo.ArgumentList.Add("--no-first-run");
		startInfo.ArgumentList.Add("--no-default-browser-check");

		try
		{
			using var process = Process.Start(startInfo);
			return process is not null;
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			return false;
		}
	}

	private static JsonObject Args(params (string Key, object? Value)[] pairs)
	{
		var node = new JsonObject();

		foreach (var (key, value) in pairs)
		{
			node[key] = value switch
			{
				null => null,
				JsonNode already => already,
				_ => JsonValue.Create(value),
			};
		}

		return node;
	}

	/// <summary>Opens a page, or reads the one that is already open.</summary>
	public sealed class BrowserNavigateTool : ITool
	{
		public string Name => "browser_navigate";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Opens a web page in the controlled browser, or reads the current page when no "
				+ "address is given.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["url"] = new JsonObject { ["type"] = "string", ["description"] = "The address to open. Optional." },
				},
			},
		};

		public async Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var url = arguments["url"]?.GetValue<string>();

			// The address is checked before the browser is touched. Connecting launches a browser, so a
			// request that is going to be refused anyway must not be the reason a window appears.
			if (!string.IsNullOrWhiteSpace(url)
				&& (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
					|| parsed.Scheme is not ("http" or "https")))
			{
				return ToolOutcome.Failure($"'{url}' is not an http address.");
			}

			// Reading the page that is already open is browser_read's job. Navigating without an address
			// would mean deciding for the model what "current" means, and launching a browser to find out.
			if (string.IsNullOrWhiteSpace(url))
			{
				return ToolOutcome.Failure("Give an address to open, or use browser_read for the current page.");
			}

			var (browser, error) = await ConnectAsync(cancellationToken).ConfigureAwait(false);

			if (browser is null)
			{
				return ToolOutcome.Failure(error ?? "The browser is not available.");
			}

			var target = url!;

			try
			{
				return ToolOutcome.Success(
					await browser.NavigateAsync(target, cancellationToken).ConfigureAwait(false));
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				// The browser's own reason, because "net::ERR_NAME_NOT_RESOLVED" is what tells the model to
				// try a different spelling and "the navigation failed" is what tells it to try again.
				return ToolOutcome.Failure(exception.Message);
			}
		}
	}

	/// <summary>Reads what is on the current page as text.</summary>
	public sealed class BrowserReadTool : ITool
	{
		public string Name => "browser_read";

		public bool RequiresConfirmation => false;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Reads the current page in the controlled browser as text.",
			Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
		};

		public async Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var (browser, error) = await ConnectAsync(cancellationToken).ConfigureAwait(false);

			if (browser is null)
			{
				return ToolOutcome.Failure(error ?? "The browser is not available.");
			}

			var url = await browser.CurrentUrlAsync(cancellationToken).ConfigureAwait(false);
			var text = await browser.ReadPageAsync(cancellationToken).ConfigureAwait(false);

			return ToolOutcome.Success($"{url}\n\n{text}");
		}
	}

	/// <summary>Presses a key in the browser.</summary>
	public sealed class BrowserKeyTool : ITool
	{
		public string Name => "browser_key";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Sends a key to the controlled browser, such as enter, tab, escape or ctrl+s.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["key"] = new JsonObject
					{
						["type"] = "string",
						["description"] = "A key name, or a combination like ctrl+s.",
					},
				},
				["required"] = new JsonArray("key"),
			},
		};

		public async Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var key = arguments["key"]?.GetValue<string>()?.Trim().ToLowerInvariant();

			if (string.IsNullOrWhiteSpace(key))
			{
				return ToolOutcome.Failure("No key was named.");
			}

			var (browser, error) = await ConnectAsync(cancellationToken).ConfigureAwait(false);

			if (browser is null)
			{
				return ToolOutcome.Failure(error ?? "The browser is not available.");
			}

			var sent = await browser.SendKeyAsync(key, cancellationToken).ConfigureAwait(false);

			return sent
				? ToolOutcome.Success($"Sent {key}.")
				: ToolOutcome.Failure($"'{key}' was not accepted.");
		}
	}

	/// <summary>Types into the controlled browser.</summary>
	public sealed class BrowserTypeTool : ITool
	{
		public string Name => "browser_type";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Types text into the controlled browser.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["text"] = new JsonObject { ["type"] = "string", ["description"] = "The text to type." },
				},
				["required"] = new JsonArray("text"),
			},
		};

		public async Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var text = arguments["text"]?.GetValue<string>();

			if (string.IsNullOrEmpty(text))
			{
				return ToolOutcome.Failure("There was no text to type.");
			}

			var (browser, error) = await ConnectAsync(cancellationToken).ConfigureAwait(false);

			if (browser is null)
			{
				return ToolOutcome.Failure(error ?? "The browser is not available.");
			}

			await browser.TypeAsync(text, cancellationToken).ConfigureAwait(false);

			return ToolOutcome.Success($"Typed {text.Length} character(s).");
		}
	}

	/// <summary>Clicks an element the page identifies by its visible text.</summary>
	public sealed class BrowserClickTool : ITool
	{
		public string Name => "browser_click";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Clicks the first element on the page whose text matches, such as a link or "
				+ "a button.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["text"] = new JsonObject { ["type"] = "string", ["description"] = "The text of the element to click." },
					["exact"] = new JsonObject
					{
						["type"] = "boolean",
						["description"] = "Require the whole text to match rather than containing it.",
					},
				},
				["required"] = new JsonArray("text"),
			},
		};

		public async Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var text = arguments["text"]?.GetValue<string>();

			if (string.IsNullOrWhiteSpace(text))
			{
				return ToolOutcome.Failure("No element text was given.");
			}

			var exact = arguments["exact"]?.GetValue<bool>() == true;
			var (browser, error) = await ConnectAsync(cancellationToken).ConfigureAwait(false);

			if (browser is null)
			{
				return ToolOutcome.Failure(error ?? "The browser is not available.");
			}

			var clicked = await browser.ClickTextAsync(text, exact, cancellationToken).ConfigureAwait(false);

			return clicked
				? ToolOutcome.Success($"Clicked '{text}'.")
				: ToolOutcome.Failure($"Nothing on the page reads '{text}'.");
		}
	}

	/// <summary>Lists what the controlled browser has open.</summary>
	public sealed class BrowserTabsTool : ITool
	{
		public string Name => "browser_tabs";

		public bool RequiresConfirmation => false;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Lists the pages open in the controlled browser.",
			Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
		};

		public async Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var (browser, error) = await ConnectAsync(cancellationToken).ConfigureAwait(false);

			if (browser is null)
			{
				return ToolOutcome.Failure(error ?? "The browser is not available.");
			}

			var tabs = await ThBrowser.TabsAsync(DebuggingPort, cancellationToken).ConfigureAwait(false);

			return tabs.Count == 0
				? ToolOutcome.Failure("No pages are open.")
				: ToolOutcome.Success(string.Join('\n', tabs.Select((tab, index) => $"{index + 1}. {tab}")));
		}
	}
}

/// <summary>
/// One connection to the browser over the DevTools Protocol.
/// <para>
/// Messages are request and response pairs with a shared counter, and events are everything else arriving on
/// the same socket. A response is matched to its request by id; anything that is not a response is an event
/// and is dropped, because nothing in these tools waits for one.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ThBrowser : IDisposable
{
	/// <summary>Typing is bounded per call, matching the desktop keyboard tool.</summary>
	private const int MaximumCharacters = 8_000;

	/// <summary>How long one DevTools command may take to be answered.</summary>
	private static TimeSpan CommandTimeout => TimeSpan.FromSeconds(15);
	private readonly ClientWebSocket _socket = new();
	private readonly SemaphoreSlim _send = new(1, 1);
	private int _nextId;

	private readonly int _port;

	private ThBrowser(ClientWebSocket socket, int port)
	{
		_socket = socket;
		_port = port;
	}

	public bool IsConnected => _socket.State == WebSocketState.Open;

	/// <summary>Reads the debugging address, which answers only when a browser is already listening.</summary>
	internal static async Task<string?> ReadVersionAsync(int port, CancellationToken cancellationToken)
	{
		try
		{
			using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(2));

			var json = await client.GetStringAsync($"http://127.0.0.1:{port}/json/version", timeout.Token).ConfigureAwait(false);
			return json;
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
		{
			return null;
		}
	}

	/// <summary>
	/// Attaches to the first page target. The browser always has at least one, so this does not have to
	/// create anything.
	/// </summary>
	internal static async Task<ThBrowser> CreateAsync(int port, CancellationToken cancellationToken)
	{
		using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
		var targets = await client.GetStringAsync($"http://127.0.0.1:{port}/json/list", cancellationToken).ConfigureAwait(false);

		if (JsonNode.Parse(targets) is not JsonArray list)
		{
			throw new InvalidOperationException("The browser returned no page list.");
		}

		var webSocketUrl = list
			.OfType<JsonObject>()
			.Where(target => target["type"]?.GetValue<string>() == "page")
			.Select(target => target["webSocketDebuggerUrl"]?.GetValue<string>())
			.FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));

		if (webSocketUrl is null)
		{
			throw new InvalidOperationException("The browser has no page open.");
		}

		var browser = new ThBrowser(new ClientWebSocket(), port);
		await browser._socket.ConnectAsync(new Uri(webSocketUrl), cancellationToken).ConfigureAwait(false);
		return browser;
	}

	/// <summary>
	/// Sends one message and waits for the reply with the same id. Events arriving meanwhile are discarded,
	/// which is why the read loop cannot be a shared background reader: nothing here needs one.
	/// </summary>
	private async Task<JsonObject> SendAsync(JsonObject message, CancellationToken cancellationToken)
	{
		var id = Interlocked.Increment(ref _nextId);
		message["id"] = id;

		var payload = Encoding.UTF8.GetBytes(message.ToJsonString());

		await _send.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			await _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_send.Release();
		}

		// Bounded, because a DevTools round trip has no deadline of its own: the loop discards every reply
		// that is not the one being waited for, so a browser that answers something else on every command
		// would otherwise spin here until the caller gave up.
		using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		budget.CancelAfter(CommandTimeout);

		while (true)
		{
			JsonObject? reply;

			try
			{
				reply = await ReceiveAsync(budget.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				throw new TimeoutException(
					$"The browser did not answer within {CommandTimeout.TotalSeconds:0} seconds.");
			}

			if (reply is not null
				&& reply["id"]?.GetValue<int>() is { } replyId
				&& replyId == id)
			{
				if (reply["error"] is JsonObject error)
				{
					throw new InvalidOperationException(
						error["message"]?.GetValue<string>() ?? "The browser rejected the command.");
				}

				return reply["result"] as JsonObject ?? [];
			}
		}
	}


	private async Task<JsonObject?> ReceiveAsync(CancellationToken cancellationToken)
	{
		var buffer = new byte[64 * 1024];
		using var assembled = new MemoryStream();

		while (true)
		{
			var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

			if (result.MessageType == WebSocketMessageType.Close)
			{
				return null;
			}

			assembled.Write(buffer, 0, result.Count);

			if (result.EndOfMessage)
			{
				break;
			}
		}

		try
		{
			return JsonNode.Parse(Encoding.UTF8.GetString(assembled.ToArray())) as JsonObject;
		}
		catch (JsonException)
		{
			// A frame that is not JSON is not something these tools can act on, and failing to parse it is
			// not a reason to end the session.
			return null;
		}
	}

	internal async Task<string> CurrentUrlAsync(CancellationToken cancellationToken) =>
		await EvaluateAsync("location.href", cancellationToken).ConfigureAwait(false) ?? "(unknown)";

	/// <summary>
	/// Reads the page. The expression asks the page for its own text content, which is the only way to get
	/// what a user would actually see without shipping an HTML reader into the browser's own context.
	/// </summary>
	internal async Task<string> ReadPageAsync(CancellationToken cancellationToken)
	{
		const string Expression = """
			(() => {
				const keep = 'script,style,noscript,svg,head';
				const clone = document.body.cloneNode(true);
				for (const node of clone.querySelectorAll(keep)) { node.remove(); }
				const title = document.title ? `Title: ${document.title}` : '';
				return [title, clone.innerText || clone.textContent || ''].join('\n').trim();
			})()
			""";

		var text = await EvaluateAsync(Expression, cancellationToken).ConfigureAwait(false);

		return string.IsNullOrWhiteSpace(text)
			? "The page has no readable text."
			: WebTools.ToReadableText(text, "text/plain");
	}

	private async Task<string?> EvaluateAsync(string expression, CancellationToken cancellationToken)
	{
		var result = await SendAsync(
			new JsonObject
			{
				["method"] = "Runtime.evaluate",
				["params"] = new JsonObject
				{
					["expression"] = expression,
					["returnByValue"] = true,
					["awaitPromise"] = true,
				},
			},
			cancellationToken).ConfigureAwait(false);

		if (result["result"]?["value"] is not JsonValue value)
		{
			return null;
		}

		return value.ToString();
	}

	/// <summary>
	/// The connected instance, used by the static helpers above so that a page read does not need to be
	/// threaded through every caller.
	/// </summary>
	private static ThBrowser? Current { get; set; }

	internal static void SetCurrent(ThBrowser browser) => Current = browser;

	/// <summary>
	/// Navigates and returns the page's text, or throws with the browser's own reason.
	/// <para>
	/// Throwing rather than returning a sentence about the failure. The sentence used to be handed back
	/// inside a success, so a typo'd host or a <c>net::ERR_*</c> arrived as "The page could not be opened:
	/// ..." wrapped in a result claiming the navigation happened, and the caller read whatever page was
	/// already open and carried on as if it were the one asked for.
	/// </para>
	/// </summary>
	internal async Task<string> NavigateAsync(string url, CancellationToken cancellationToken)
	{
		var result = await SendAsync(
			new JsonObject { ["method"] = "Page.navigate", ["params"] = new JsonObject { ["url"] = url } },
			cancellationToken).ConfigureAwait(false);

		if (result["errorText"] is JsonValue failure)
		{
			throw new InvalidOperationException($"The page could not be opened: {failure.ToString()}");
		}

		// The load event arrives after the navigate reply, so the page is read afterwards rather than from
		// the reply, which only says the navigation was accepted.
		await WaitForLoadAsync(cancellationToken).ConfigureAwait(false);

		return await ReadPageAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task WaitForLoadAsync(CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow.AddSeconds(20);

		while (DateTime.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var state = await EvaluateAsync("document.readyState", cancellationToken).ConfigureAwait(false);

			if (state == "complete")
			{
				return;
			}

			await Task.Delay(150, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Sends a key, holding the earlier keys in the combination down while the last one is pressed. The
	/// protocol wants each physical key sent separately with a modifier count, which is why this does not
	/// send one event with a chord.
	/// </summary>
	internal async Task<bool> SendKeyAsync(string key, CancellationToken cancellationToken)
	{
		const int KeyDown = 1;
		const int KeyUp = 2;
		const int CharType = 3;

		var names = key.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		if (names.Length == 0)
		{
			return false;
		}

		var codes = new int[names.Length];

		for (var index = 0; index < names.Length; index++)
		{
			if (KeyCode(names[index]) is not { } code)
			{
				return false;
			}

			codes[index] = code;
		}

		var modifiers = 0;

		for (var index = 0; index < names.Length - 1; index++)
		{
			modifiers |= ModifierBit(codes[index]);

			await DispatchKeyAsync(
				names[index], codes[index], KeyDown, modifiers, null, cancellationToken).ConfigureAwait(false);
		}

		var last = names.Length - 1;
		var text = names[last].Length == 1 ? names[last] : null;

		await DispatchKeyAsync(names[last], codes[last], KeyDown, modifiers, text, cancellationToken).ConfigureAwait(false);

		if (text is not null)
		{
			// A printable key produces a text event as well, and a browser that only sees the key events
			// inserts nothing.
			await DispatchKeyAsync(names[last], codes[last], CharType, modifiers, text, cancellationToken).ConfigureAwait(false);
		}

		await DispatchKeyAsync(names[last], codes[last], KeyUp, modifiers, null, cancellationToken).ConfigureAwait(false);

		for (var index = names.Length - 2; index >= 0; index--)
		{
			modifiers &= ~ModifierBit(codes[index]);

			await DispatchKeyAsync(
				names[index], codes[index], KeyUp, modifiers, null, cancellationToken).ConfigureAwait(false);
		}

		return true;
	}

	private async Task DispatchKeyAsync(
		string name,
		int code,
		int type,
		int modifiers,
		string? text,
		CancellationToken cancellationToken)
	{
		var parameters = new JsonObject
		{
			["type"] = type switch
			{
				1 => "keyDown",
				2 => "keyUp",
				_ => "char",
			},
			["key"] = name,
			["code"] = name,
			["windowsVirtualKeyCode"] = code,
			["modifiers"] = modifiers,
		};

		if (text is not null)
		{
			parameters["text"] = text;
		}

		await SendAsync(
			new JsonObject { ["method"] = "Input.dispatchKeyEvent", ["params"] = parameters },
			cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// The modifier mask the protocol expects. It is a count of held modifiers rather than a set of flags,
	/// which is why the bit for each key has to be set in order: Alt is 1, Control is 2, Meta is 4 and Shift
	/// is 8.
	/// </summary>
	private static int ModifierBit(int code) => code switch
	{
		18 => 1,
		17 => 2,
		91 or 92 => 4,
		16 => 8,
		_ => 0,
	};

	private static int? KeyCode(string name) => name switch
	{
		"enter" or "return" => 13,
		"tab" => 9,
		"escape" or "esc" => 27,
		"backspace" => 8,
		"delete" => 46,
		"space" => 32,
		"up" => 38,
		"down" => 40,
		"left" => 37,
		"right" => 39,
		"pageup" => 33,
		"pagedown" => 34,
		"home" => 36,
		"end" => 35,
		"shift" => 16,
		"ctrl" or "control" => 17,
		"alt" => 18,
		"win" or "meta" => 91,
		_ => name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])
			? char.ToUpperInvariant(name[0])
			: null,
	};

	internal async Task TypeAsync(string text, CancellationToken cancellationToken)
	{
		// Capped, like the desktop keyboard tool. Each character costs two DevTools round trips, so an
		// unbounded length is a request to make two hundred thousand of them, and a model that miscounts
		// would otherwise be able to occupy the turn for as long as it liked.
		if (text.Length > MaximumCharacters)
		{
			throw new ArgumentOutOfRangeException(
				nameof(text),
				$"Typing is limited to {MaximumCharacters} characters at a time; this request was {text.Length}.");
		}

		foreach (var character in text)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var value = character.ToString();

			await SendAsync(
				new JsonObject
				{
					["method"] = "Input.dispatchKeyEvent",
					["params"] = new JsonObject
					{
						["type"] = "keyDown",
						["text"] = value,
						["key"] = value,
					},
				},
				cancellationToken).ConfigureAwait(false);

			await SendAsync(
				new JsonObject
				{
					["method"] = "Input.dispatchKeyEvent",
					["params"] = new JsonObject
					{
						["type"] = "keyUp",
						["key"] = value,
					},
				},
				cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Clicks the first element whose text matches. The click is dispatched from inside the page at the
	/// element's own coordinates, which is what makes it reach a button that has moved since it was found.
	/// </summary>
	/// <summary>
	/// Builds the page-side script as plain concatenation rather than an interpolated literal, because the
	/// script is full of braces and every one of them would otherwise need escaping.
	/// </summary>
	private static string BuildClickExpression(string text, bool exact)
	{
		var needle = JsonValue.Create(text)?.ToJsonString() ?? "\"\"";
		// The text the model asked for is the needle and the element's own text is the haystack. Reversed, a
	// request for "Sign" would never match a button labelled "Sign in", which is the common case.
	var comparison = exact ? "t === wanted" : "t.includes(wanted)";

		return "(() => {"
			+ $"const wanted = {needle};"
			+ "const selector = 'a,button,[role=button],input[type=submit],[onclick],summary,label';"
			+ "for (const element of document.querySelectorAll(selector)) {"
			+ "const t = (element.innerText || element.value || '').trim();"
			+ $"if (t.length === 0 || !({comparison})) {{ continue; }}"
			+ "element.scrollIntoView({ block: 'center' });"
			+ "const box = element.getBoundingClientRect();"
			+ "return JSON.stringify({ x: box.left + box.width / 2, y: box.top + box.height / 2 });"
			+ "}"
			+ "return '';"
			+ "})()";
	}

	internal static string BuildClickExpressionForTest(string text, bool exact = false) =>
		BuildClickExpression(text, exact);

	internal async Task<bool> ClickTextAsync(string text, bool exact, CancellationToken cancellationToken)
	{
		var found = await EvaluateAsync(
			BuildClickExpression(text, exact),
			cancellationToken).ConfigureAwait(false);

		if (string.IsNullOrWhiteSpace(found) || JsonNode.Parse(found) is not JsonObject point)
		{
			return false;
		}

		var x = point["x"]!.GetValue<double>();
		var y = point["y"]!.GetValue<double>();

		const int LeftPressed = 1;
		const int LeftReleased = 2;

		foreach (var type in new[] { LeftPressed, LeftReleased })
		{
			await SendAsync(
				new JsonObject
				{
					["method"] = "Input.dispatchMouseEvent",
					["params"] = new JsonObject
					{
						["type"] = type == LeftPressed ? "mousePressed" : "mouseReleased",
						["x"] = x,
						["y"] = y,
						["button"] = "left",
						["clickCount"] = 1,
					},
				},
				cancellationToken).ConfigureAwait(false);
		}

		return true;
	}

	internal static async Task<List<string>> TabsAsync(int port, CancellationToken cancellationToken)
	{
		using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
		var targets = await client.GetStringAsync("http://127.0.0.1:9333/json/list", cancellationToken).ConfigureAwait(false);

		if (JsonNode.Parse(targets) is not JsonArray list)
		{
			return [];
		}

		return
		[
			.. list.OfType<JsonObject>()
				.Where(target => target["type"]?.GetValue<string>() == "page")
				.Select(target => $"{target["title"]?.GetValue<string>() ?? "(untitled)"} - {target["url"]?.GetValue<string>() ?? string.Empty}"),
		];
	}

	public void Dispose() => _socket.Dispose();
}
