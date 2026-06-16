namespace PiSharp.Runtime;

public enum RuntimeDiagnosticType { Warning, Error }

public sealed record RuntimeDiagnostic(RuntimeDiagnosticType Type, string Message);
