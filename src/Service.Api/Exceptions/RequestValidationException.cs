namespace Service.Api.Exceptions;

/// <summary>Application validation failed. The message must be safe for clients.</summary>
public sealed class RequestValidationException(string message) : Exception(message);
