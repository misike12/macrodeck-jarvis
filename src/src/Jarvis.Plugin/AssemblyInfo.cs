using System.Runtime.Versioning;

// The manifest declares a single win-x64 entrypoint and every action reports MacroDeckPlatform.Windows,
// so the assembly genuinely is Windows-only. Declaring it once here is what lets the analyser accept the
// Windows-only audio APIs instead of every call site needing its own suppression.
[assembly: SupportedOSPlatform("windows")]