using System.Net;

namespace CmdManager.Core.Http;

/// <summary>A non-success response from the CmdManager API; <see cref="Exception.Message"/> is the server's problem detail.</summary>
public sealed class ApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public bool IsUnauthorized => StatusCode == HttpStatusCode.Unauthorized;
    public bool IsConflict => StatusCode == HttpStatusCode.Conflict;
    public bool IsNotFound => StatusCode == HttpStatusCode.NotFound;
}
