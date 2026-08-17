namespace SlimDep.Core.Exceptions;

/// <summary>Thrown when SlimDep cannot complete analysis due to a known error condition.</summary>
public sealed class SlimDepException : Exception
{
    public SlimDepException(string message) : base(message) { }
    public SlimDepException(string message, Exception inner) : base(message, inner) { }
}
