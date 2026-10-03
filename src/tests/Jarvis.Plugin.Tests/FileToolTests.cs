using System.Text.Json.Nodes;
using Jarvis.Plugin.Llm;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The file tools, run against a real temporary directory. Deletion and moving are exercised for real
/// because the interesting failures are the ones about what is actually on disk, not about a mock.
/// </summary>
[TestFixture]
public class FileToolTests
{
	private string _root = null!;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), $"jarvis-file-{Guid.CreateVersion7():N}");
		Directory.CreateDirectory(_root);
	}

	[TearDown]
	public void TearDown()
	{
		try
		{
			if (Directory.Exists(_root))
			{
				Directory.Delete(_root, recursive: true);
			}
		}
		catch (IOException)
		{
			// A leftover temp directory is not worth failing over.
		}
	}

	private string Write(string name, string content = "hello")
	{
		var path = Path.Combine(_root, name);
		File.WriteAllText(path, content);
		return path;
	}

	private static JsonObject Args(params (string Key, object? Value)[] pairs)
	{
		var node = new JsonObject();

		foreach (var (key, value) in pairs)
		{
			node[key] = value is null ? null : JsonValue.Create(value);
		}

		return node;
	}

	private static Task<ToolOutcome> Invoke(ITool tool, JsonObject arguments) =>
		tool.InvokeAsync(arguments, CancellationToken.None);

	[Test]
	public async Task A_missing_file_is_reported_as_missing_rather_than_as_an_error()
	{
		var outcome = await Invoke(new FileTools.FileExistsTool(), Args(("path", Path.Combine(_root, "nope.txt"))));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.True, "not finding something is an answer, not a failure");
			Assert.That(outcome.Content, Does.Contain("does not exist"));
		});
	}

	[Test]
	public async Task An_existing_file_is_described()
	{
		var path = Write("present.txt", "some content");

		var outcome = await Invoke(new FileTools.FileExistsTool(), Args(("path", path)));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.True);
			Assert.That(outcome.Content, Does.Contain("is a file"));
			Assert.That(outcome.Content, Does.Contain("bytes"));
		});
	}

	[Test]
	public async Task An_existing_folder_is_described_as_a_folder()
	{
		var outcome = await Invoke(new FileTools.FileExistsTool(), Args(("path", _root)));

		Assert.That(outcome.Content, Does.Contain("is a folder"));
	}

	[Test]
	public async Task Every_file_tool_refuses_a_blank_path()
	{
		foreach (var tool in new ITool[]
		{
			new FileTools.FileExistsTool(),
			new FileTools.DeletePathTool(),
		})
		{
			var outcome = await Invoke(tool, Args(("path", "   ")));
			Assert.That(outcome.Ok, Is.False, $"{tool.Name} accepted a blank path");
		}

		foreach (var tool in new ITool[] { new FileTools.MovePathTool() })
		{
			var outcome = await Invoke(tool, Args(("from", ""), ("to", _root)));
			Assert.That(outcome.Ok, Is.False, $"{tool.Name} accepted a blank path");
		}
	}

	[Test]
	public async Task Searching_finds_by_name_pattern()
	{
		Write("alpha.cs");
		Write("beta.cs");
		Write("gamma.txt");

		var outcome = await Invoke(
			new FileTools.SearchFilesTool(),
			Args(("directory", _root), ("namePattern", "*.cs")));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.True, outcome.Content);
			Assert.That(outcome.Content, Does.Contain("alpha.cs"));
			Assert.That(outcome.Content, Does.Contain("beta.cs"));
			Assert.That(outcome.Content, Does.Not.Contain("gamma.txt"));
		});
	}

	[Test]
	public async Task Searching_finds_by_content()
	{
		Write("one.txt", "the quick brown fox");
		Write("two.txt", "something else entirely");

		var outcome = await Invoke(
			new FileTools.SearchFilesTool(),
			Args(("directory", _root), ("contains", "brown")));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.True, outcome.Content);
			Assert.That(outcome.Content, Does.Contain("one.txt"));
			Assert.That(outcome.Content, Does.Not.Contain("two.txt"));
		});
	}

	/// <summary>Nothing matching is an answer, and it must be worded so the model can explain it.</summary>
	[Test]
	public async Task A_search_that_matches_nothing_says_so()
	{
		var outcome = await Invoke(
			new FileTools.SearchFilesTool(),
			Args(("directory", _root), ("namePattern", "*.nothing")));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("Nothing matched"));
		});
	}

	[Test]
	public async Task Searching_a_folder_that_is_not_there_fails_cleanly()
	{
		var outcome = await Invoke(
			new FileTools.SearchFilesTool(),
			Args(("directory", Path.Combine(_root, "missing"))));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("is not a folder"));
		});
	}

	[Test]
	public async Task Moving_renames_a_file()
	{
		var from = Write("before.txt");
		var to = Path.Combine(_root, "after.txt");

		var outcome = await Invoke(new FileTools.MovePathTool(), Args(("from", from), ("to", to)));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.True, outcome.Content);
			Assert.That(File.Exists(to), Is.True);
			Assert.That(File.Exists(from), Is.False);
		});
	}

	[Test]
	public async Task Moving_creates_the_destination_folder_when_it_is_missing()
	{
		var from = Write("moved.txt");
		var to = Path.Combine(_root, "new", "deeper", "moved.txt");

		var outcome = await Invoke(new FileTools.MovePathTool(), Args(("from", from), ("to", to)));

		Assert.That(outcome.Ok, Is.True, outcome.Content);
		Assert.That(File.Exists(to), Is.True);
	}

	/// <summary>A move that overwrites its destination is a delete with extra steps, so it is refused.</summary>
	[Test]
	public async Task Moving_refuses_to_overwrite_something_that_is_already_there()
	{
		var from = Write("source.txt", "source");
		var to = Write("destination.txt", "do not lose me");

		var outcome = await Invoke(new FileTools.MovePathTool(), Args(("from", from), ("to", to)));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("already exists"));
			Assert.That(File.ReadAllText(to), Is.EqualTo("do not lose me"));
		});
	}

	[Test]
	public async Task Moving_something_that_is_not_there_fails_cleanly()
	{
		var outcome = await Invoke(
			new FileTools.MovePathTool(),
			Args(("from", Path.Combine(_root, "nope.txt")), ("to", Path.Combine(_root, "x.txt"))));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("does not exist"));
		});
	}

	[Test]
	public async Task Deleting_removes_a_file()
	{
		var path = Write("doomed.txt");

		var outcome = await Invoke(new FileTools.DeletePathTool(), Args(("path", path)));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.True, outcome.Content);
			Assert.That(File.Exists(path), Is.False);
		});
	}

	/// <summary>
	/// The one that matters: a populated folder is not deleted by accident. Recursing has to be asked for.
	/// </summary>
	[Test]
	public async Task Deleting_a_populated_folder_is_refused_without_recursion()
	{
		Write("keep.txt");

		var outcome = await Invoke(new FileTools.DeletePathTool(), Args(("path", _root)));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("recursive"));
			Assert.That(Directory.Exists(_root), Is.True, "the folder was deleted anyway");
			Assert.That(File.Exists(Path.Combine(_root, "keep.txt")), Is.True);
		});
	}

	[Test]
	public async Task Deleting_an_empty_folder_works_without_recursion()
	{
		var empty = Path.Combine(_root, "empty");
		Directory.CreateDirectory(empty);

		var outcome = await Invoke(new FileTools.DeletePathTool(), Args(("path", empty)));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.True, outcome.Content);
			Assert.That(Directory.Exists(empty), Is.False);
		});
	}

	[Test]
	public async Task Deleting_recursively_removes_the_contents_when_asked()
	{
		Write("keep.txt");
		Directory.CreateDirectory(Path.Combine(_root, "nested"));
		File.WriteAllText(Path.Combine(_root, "nested", "deep.txt"), "x");

		var outcome = await Invoke(
			new FileTools.DeletePathTool(),
			Args(("path", _root), ("recursive", true)));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.True, outcome.Content);
			Assert.That(Directory.Exists(_root), Is.False);
		});
	}

	[Test]
	public async Task Deleting_something_that_is_not_there_fails_cleanly()
	{
		var outcome = await Invoke(
			new FileTools.DeletePathTool(),
			Args(("path", Path.Combine(_root, "never-existed"))));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("does not exist"));
		});
	}

	/// <summary>
	/// Deleting and moving both act, so both must be gated. A read-only file tool set would be safer but
	/// would not be able to do the job.
	/// </summary>
	[Test]
	public void File_tools_are_gated_by_class()
	{
		Assert.Multiple(() =>
		{
			Assert.That(new FileTools.FileExistsTool().RequiresConfirmation, Is.False);
			Assert.That(new FileTools.SearchFilesTool().RequiresConfirmation, Is.False);
			Assert.That(new FileTools.MovePathTool().RequiresConfirmation, Is.True);
			Assert.That(new FileTools.DeletePathTool().RequiresConfirmation, Is.True);
		});
	}
}
