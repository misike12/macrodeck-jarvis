using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Plugin.Core;

namespace Jarvis.Plugin.Llm;

/// <summary>
/// The remaining file tools, on top of the read and write pair that already existed.
/// <para>
/// Every path goes through <see cref="FileGuard"/>, so a tool added here cannot become a way around the
/// protection the others have. Deletion and moving are separated rather than combined on purpose: moving is
/// usually recoverable by moving it back, deleting is not, and lumping them together would let a model
/// destroy something with what the user believed was a rename.
/// </para>
/// </summary>
public static partial class FileTools
{
	/// <summary>Answers whether a path exists and what it is. Never throws for a path that simply is not there.</summary>
	public sealed class FileExistsTool : ITool
	{
		public string Name => "file_exists";

		public bool RequiresConfirmation => false;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Checks whether a path exists and whether it is a file or a folder.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path to check." },
				},
				["required"] = new JsonArray("path"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var path = arguments["path"]?.GetValue<string>();

			if (string.IsNullOrWhiteSpace(path))
			{
				return Task.FromResult(ToolOutcome.Failure("No path was given."));
			}

			if (FileGuard.Check(path) is { } denial)
			{
				return Task.FromResult(ToolOutcome.Failure(denial));
			}

			if (Directory.Exists(path))
			{
				return Task.FromResult(ToolOutcome.Success($"{path} is a folder."));
			}

			if (File.Exists(path))
			{
				var info = new FileInfo(path);
				return Task.FromResult(ToolOutcome.Success(
					$"{path} is a file, {info.Length} bytes, last changed {info.LastWriteTimeUtc:yyyy-MM-dd}."));
			}

			return Task.FromResult(ToolOutcome.Success($"{path} does not exist."));
		}
	}

	/// <summary>Finds files by name, by content, or both.</summary>
	public sealed class SearchFilesTool : ITool
	{
		/// <summary>
		/// Bounded because a content search over a whole drive is an operation that can run for an hour and
		/// fill memory. A cap that the caller did not ask for is better than an unbounded walk.
		/// </summary>
		private const int DefaultLimit = 200;

		private const int MaximumLimit = 5_000;

		/// <summary>Files above this are skipped by a content search rather than being read whole.</summary>
		private const long MaximumContentBytes = 4L * 1024 * 1024;

		public string Name => "search_files";

		public bool RequiresConfirmation => false;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Finds files under a folder, by name pattern, by text inside them, or both.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["directory"] = new JsonObject { ["type"] = "string", ["description"] = "Folder to search. Defaults to the user profile." },
					["namePattern"] = new JsonObject { ["type"] = "string", ["description"] = "Wildcard name filter, for example *.cs." },
					["contains"] = new JsonObject { ["type"] = "string", ["description"] = "Text the file must contain." },
					["limit"] = new JsonObject { ["type"] = "integer", ["description"] = $"Maximum results, default {DefaultLimit}." },
				},
			},
		};

		public async Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var directory = arguments["directory"]?.GetValue<string>();
			var namePattern = arguments["namePattern"]?.GetValue<string>();
			var contains = arguments["contains"]?.GetValue<string>();
			var limit = arguments["limit"]?.GetValue<int?>() ?? DefaultLimit;

			if (string.IsNullOrWhiteSpace(directory))
			{
				directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			}

			if (FileGuard.Check(directory) is { } denial)
			{
				return ToolOutcome.Failure(denial);
			}

			if (!Directory.Exists(directory))
			{
				return ToolOutcome.Failure($"{directory} is not a folder.");
			}

			limit = Math.Clamp(limit, 1, MaximumLimit);
			var results = new List<string>();
			var truncated = false;

			// AllDirectories, with errors ignored: one unreadable folder must not stop the whole search.
			var options = new EnumerationOptions
			{
				RecurseSubdirectories = true,
				IgnoreInaccessible = true,
				MaxRecursionDepth = 32,
			};

			IEnumerator<string> files;

			try
			{
				files = Directory.EnumerateFiles(directory, namePattern ?? "*", options).GetEnumerator();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
			{
				return ToolOutcome.Failure($"The folder could not be searched: {exception.Message}");
			}

			using (files)
			{
				while (results.Count < limit && files.MoveNext())
				{
					cancellationToken.ThrowIfCancellationRequested();

					var path = files.Current;

					if (FileGuard.Check(path) is not null)
					{
						continue;
					}

					if (!string.IsNullOrWhiteSpace(contains) && !Contains(path, contains))
					{
						continue;
					}

					results.Add(path);
				}

				truncated = files.MoveNext();
			}

			if (results.Count == 0)
			{
				return ToolOutcome.Failure("Nothing matched.");
			}

			var builder = new StringBuilder();

			foreach (var result in results)
			{
				builder.Append(result).Append('\n');
			}

			builder.Append(truncated ? "\n[more results were not shown]" : $"\n{results.Count} result(s)");

			return ToolOutcome.Success(builder.ToString());
		}

		private static bool Contains(string path, string needle)
		{
			try
			{
				var info = new FileInfo(path);

				if (info.Length is 0 or > MaximumContentBytes)
				{
					// A huge file would be read into memory for a search that probably does not want it.
					return false;
				}

				return File.ReadAllText(path).Contains(needle, StringComparison.OrdinalIgnoreCase);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				return false;
			}
		}
	}

	/// <summary>Moves or renames. Recoverable, unlike deletion.</summary>
	public sealed class MovePathTool : ITool
	{
		public string Name => "move_path";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Moves a file or folder, which is also how you rename one. Does not overwrite an "
				+ "existing destination.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["from"] = new JsonObject { ["type"] = "string", ["description"] = "Existing absolute path." },
					["to"] = new JsonObject { ["type"] = "string", ["description"] = "New absolute path." },
				},
				["required"] = new JsonArray("from", "to"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var from = arguments["from"]?.GetValue<string>();
			var to = arguments["to"]?.GetValue<string>();

			if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
			{
				return Task.FromResult(ToolOutcome.Failure("Both a source and a destination are needed."));
			}

			if (FileGuard.Check(from) is { } fromDenial)
			{
				return Task.FromResult(ToolOutcome.Failure(fromDenial));
			}

			if (FileGuard.Check(to) is { } toDenial)
			{
				return Task.FromResult(ToolOutcome.Failure(toDenial));
			}

			try
			{
				var isDirectory = Directory.Exists(from);

				if (!isDirectory && !File.Exists(from))
				{
					return Task.FromResult(ToolOutcome.Failure($"{from} does not exist."));
				}

				var destinationExists = isDirectory
					? Directory.Exists(to)
					: File.Exists(to);

				if (destinationExists)
				{
					// Refused rather than overwritten: a move that silently replaces its destination is a
					// delete with extra steps.
					return Task.FromResult(ToolOutcome.Failure($"{to} already exists."));
				}

				var parent = Path.GetDirectoryName(to);

				if (!string.IsNullOrEmpty(parent))
				{
					Directory.CreateDirectory(parent);
				}

				if (isDirectory)
				{
					Directory.Move(from, to);
				}
				else
				{
					File.Move(from, to);
				}

				return Task.FromResult(ToolOutcome.Success($"Moved {from} to {to}."));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				return Task.FromResult(ToolOutcome.Failure($"The move did not work: {exception.Message}"));
			}
		}
	}

	/// <summary>
	/// Deletes. Requires a directory to be empty unless asked otherwise, because deleting a populated
	/// folder recursively is the single most destructive thing this plugin can do and it should be the
	/// hardest, not the easiest.
	/// </summary>
	public sealed class DeletePathTool : ITool
	{
		public string Name => "delete_path";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Deletes a file, or a folder. A folder is only deleted when empty unless "
				+ "recursive is true. Use move_path instead when the file might be wanted again.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path to delete." },
					["recursive"] = new JsonObject
					{
						["type"] = "boolean",
						["description"] = "Delete a folder and everything in it. Default false.",
					},
				},
				["required"] = new JsonArray("path"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var path = arguments["path"]?.GetValue<string>();
			var recursive = arguments["recursive"]?.GetValue<bool>() == true;

			if (string.IsNullOrWhiteSpace(path))
			{
				return Task.FromResult(ToolOutcome.Failure("No path was given."));
			}

			if (FileGuard.Check(path) is { } denial)
			{
				return Task.FromResult(ToolOutcome.Failure(denial));
			}

			try
			{
				if (File.Exists(path))
				{
					File.Delete(path);
					return Task.FromResult(ToolOutcome.Success($"Deleted {path}."));
				}

				if (!Directory.Exists(path))
				{
					return Task.FromResult(ToolOutcome.Failure($"{path} does not exist."));
				}

				if (!recursive && Directory.EnumerateFileSystemEntries(path).Any())
				{
					var count = Directory.EnumerateFileSystemEntries(path).Count();

					return Task.FromResult(ToolOutcome.Failure(
						$"{path} holds {count} entr{(count == 1 ? "y" : "ies")}. "
						+ "Ask for it again with recursive set to true to delete the contents too."));
				}

				Directory.Delete(path, recursive);
				return Task.FromResult(ToolOutcome.Success($"Deleted {path}."));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				return Task.FromResult(ToolOutcome.Failure($"The delete did not work: {exception.Message}"));
			}
		}
	}
}
