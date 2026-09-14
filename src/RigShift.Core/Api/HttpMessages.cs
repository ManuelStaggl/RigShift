using System.Globalization;
using System.Text;

namespace RigShift.Core.Api;

/// <summary>One parsed HTTP request. Header names are case-insensitive, query values are unescaped.</summary>
public sealed record ApiRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out string? value) ? value : null;

    /// <summary><c>true</c> for <c>?name=true</c> or a bare <c>?name</c>.</summary>
    public bool Flag(string name) =>
        Query.TryGetValue(name, out string? value)
        && (value.Length == 0 || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
}

/// <summary>A JSON response. <see cref="Body"/> is already serialized.</summary>
public sealed record ApiResponse(int StatusCode, string Body);

/// <summary>
/// Minimal HTTP/1.1 for the local API (docs/PLAN.md, section 6, item 11): one request per connection, no chunked
/// bodies, no keep-alive. Enough for curl, PowerShell, SimHub and Stream Deck plugins.
/// </summary>
public static class HttpMessages
{
    public const int MaxHeaderBytes = 8 * 1024;
    public const int MaxBodyBytes = 64 * 1024;

    private static readonly byte[] HeaderEnd = "\r\n\r\n"u8.ToArray();

    /// <returns>The request, or <c>null</c> if the client closed the connection before sending one.</returns>
    /// <exception cref="InvalidDataException">Malformed or oversized request.</exception>
    public static async Task<ApiRequest?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] buffer = new byte[MaxHeaderBytes];
        int length = 0;
        int headerEnd;
        while ((headerEnd = buffer.AsSpan(0, length).IndexOf(HeaderEnd)) < 0)
        {
            if (length == buffer.Length)
            {
                throw new InvalidDataException("Request header too large.");
            }

            int read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                return length == 0 ? null : throw new InvalidDataException("Connection closed inside the request header.");
            }

            length += read;
        }

        string[] lines = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || requestLine[1].Length == 0)
        {
            throw new InvalidDataException("Malformed request line.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw new InvalidDataException("Malformed header line.");
            }

            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        // The API reads nothing from the body, but a client that sent one must not see its connection reset.
        if (headers.TryGetValue("Content-Length", out string? contentLength))
        {
            if (!int.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out int bodyLength) || bodyLength > MaxBodyBytes)
            {
                throw new InvalidDataException("Invalid or too large Content-Length.");
            }

            int buffered = length - headerEnd - HeaderEnd.Length;
            await DiscardAsync(stream, bodyLength - buffered, cancellationToken);
        }

        (string path, Dictionary<string, string> query) = SplitTarget(requestLine[1]);
        return new ApiRequest(requestLine[0].ToUpperInvariant(), path, query, headers);
    }

    public static async Task WriteResponseAsync(Stream stream, ApiResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(response);

        byte[] body = Encoding.UTF8.GetBytes(response.Body);
        string head = string.Create(CultureInfo.InvariantCulture,
            $"HTTP/1.1 {response.StatusCode} {ReasonPhrase(response.StatusCode)}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task DiscardAsync(Stream stream, int remaining, CancellationToken cancellationToken)
    {
        byte[] scratch = new byte[4096];
        while (remaining > 0)
        {
            int read = await stream.ReadAsync(scratch.AsMemory(0, Math.Min(remaining, scratch.Length)), cancellationToken);
            if (read == 0)
            {
                return;
            }

            remaining -= read;
        }
    }

    private static (string Path, Dictionary<string, string> Query) SplitTarget(string target)
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int mark = target.IndexOf('?', StringComparison.Ordinal);
        if (mark < 0)
        {
            return (target, query);
        }

        foreach (string pair in target[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            string name = Unescape(equals < 0 ? pair : pair[..equals]);
            query[name] = equals < 0 ? string.Empty : Unescape(pair[(equals + 1)..]);
        }

        return (target[..mark], query);
    }

    private static string Unescape(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        503 => "Service Unavailable",
        _ => statusCode >= 500 ? "Internal Server Error" : "Error",
    };
}
