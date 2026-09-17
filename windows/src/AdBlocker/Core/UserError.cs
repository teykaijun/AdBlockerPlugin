namespace AdBlocker.Core;

/// <summary>A problem the user can fix; shown without a stack trace.</summary>
public sealed class UserError(string message, Exception? inner = null) : Exception(message, inner);
