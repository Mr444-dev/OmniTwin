using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OmniTwin.Perception.Tracking;

namespace OmniTwin.Backend.Calibration;

/// <summary>Serves local calibration tools and validates profiles without persisting images or changing a running mapper.</summary>
public static class CalibrationEndpoints
{
    private const int MaximumBytes = 16_384;

    /// <summary>Maps embedded UI assets and the bounded, same-origin JSON validation endpoint.</summary>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        MapAsset(app, "/calibration", "html", "text/html; charset=utf-8");
        MapAsset(app, "/calibration/calibration.css", "css", "text/css; charset=utf-8");
        MapAsset(app, "/calibration/calibration.js", "js", "text/javascript; charset=utf-8");
        app.MapPost("/api/calibration/validate", (Func<HttpContext, Task<IResult>>)ValidateAsync);
    }

    private static void MapAsset(WebApplication app, string path, string extension, string contentType)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("OmniTwin.Calibration.calibration." + extension)
            ?? throw new InvalidOperationException("Calibration UI resource is missing.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var bytes = buffer.ToArray();
        app.MapGet(path, (HttpContext context) =>
        {
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src blob:; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            return Results.Bytes(bytes, contentType);
        });
    }

    private static async Task<IResult> ValidateAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.Headers["X-OmniTwin-Calibration"].Count != 1 || request.Headers["X-OmniTwin-Calibration"] != "1" ||
            !SameOrigin(request)) return Error("origin_denied", StatusCodes.Status403Forbidden);
        if (!request.HasJsonContentType()) return Error("json_required", StatusCodes.Status415UnsupportedMediaType);
        if (request.ContentLength > MaximumBytes) return Error("profile_too_large", StatusCodes.Status413PayloadTooLarge);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var bytes = new byte[MaximumBytes + 1];
        var length = 0;
        try
        {
            while (length < bytes.Length)
            {
                var count = await request.Body.ReadAsync(bytes.AsMemory(length), deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                length += count;
            }
            if (length > MaximumBytes) return Error("profile_too_large", StatusCodes.Status413PayloadTooLarge);
            CalibrationProfile.Parse(bytes.AsMemory(0, length));
            return Results.Bytes(bytes.AsMemory(0, length).ToArray(), "application/json", "calibration.json");
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { return Error("profile_read_timeout", StatusCodes.Status408RequestTimeout); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { return Error("invalid_calibration", StatusCodes.Status400BadRequest); }
    }

    internal static bool SameOrigin(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Origin", out var origins)) return true;
        return origins.Count == 1 && Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) &&
            origin.GetLeftPart(UriPartial.Authority).Equals(origins[0], StringComparison.OrdinalIgnoreCase) &&
            origin.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase) &&
            origin.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static IResult Error(string category, int status) => Results.Json(new { error = category }, statusCode: status);
}
