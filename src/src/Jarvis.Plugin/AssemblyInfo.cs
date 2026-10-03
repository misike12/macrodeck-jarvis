using System.Runtime.Versioning;

// The manifest declares a single win-x64 entrypoint and every action reports MacroDeckPlatform.Windows,
// so the assembly genuinely is Windows-only. Declaring it once here is what lets the analyser accept the
// Windows-only audio APIs instead of every call site needing its own suppression.
[assembly: SupportedOSPlatform("windows")]

// The input tools keep their key resolution and cursor read internal because they are implementation
// detail, but they are the only way to test those decisions without synthesising input against whatever
// window happens to be focused while the suite runs.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Jarvis.Plugin.Tests")]