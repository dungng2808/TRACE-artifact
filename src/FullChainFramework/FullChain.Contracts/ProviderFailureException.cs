using System;

namespace FullChain.Contracts;

/// <summary>
/// A failure reported by a model provider or its response protocol. It is kept
/// in Contracts so orchestration can classify it without depending on an
/// infrastructure implementation.
/// </summary>
public sealed class ProviderFailureException : Exception
{
    public ProviderFailureException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
