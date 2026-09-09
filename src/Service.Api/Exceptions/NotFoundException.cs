namespace Service.Api.Exceptions;

/// <summary>An application resource was not found. The message must be safe for clients.</summary>
public class NotFoundException(string message) : Exception(message);
