using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using self_API_Gateway.Configuration;
using self_API_Gateway.Routing;

namespace self_API_Gateway.Admin;

public static class AdminEndpoints
{
    public const string ConfigPath = "/admin/config";
    public const string ApiKeyHeader = "X-Admin-Key";

    // GET returns the live Gateway configuration; PUT replaces it with the submitted one.
    // Both require the X-Admin-Key header to match Admin:ApiKey. Without a configured key the API is not mapped at all.
    public static void MapGatewayAdmin(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AdminEndpoints).FullName!);

        var apiKey = app.Configuration["Admin:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            logger.LogWarning("Admin API is disabled: set Admin:ApiKey to enable {Path}", ConfigPath);
            return;
        }

        // Compared as hashes so the check takes the same time whatever the submitted key looks like.
        var keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        var runtime = app.Services.GetRequiredService<GatewayRuntime>();

        RequestDelegate get = ctx =>
            IsAuthorized(ctx, keyHash, logger)
                ? WriteJsonAsync(ctx, StatusCodes.Status200OK, runtime.Current.Options, GatewayJsonContext.Default.GatewayOptions)
                : WriteErrorAsync(ctx, StatusCodes.Status401Unauthorized, $"Missing or invalid {ApiKeyHeader} header.");

        RequestDelegate put = async ctx =>
        {
            if (!IsAuthorized(ctx, keyHash, logger))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status401Unauthorized, $"Missing or invalid {ApiKeyHeader} header.");
                return;
            }

            GatewayOptions? options;
            try
            {
                options = await JsonSerializer.DeserializeAsync(ctx.Request.Body, GatewayJsonContext.Default.GatewayOptions, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "Invalid JSON: " + ex.Message);
                return;
            }
            if (options is null)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "The request body must be a Gateway configuration object.");
                return;
            }

            try
            {
                var result = runtime.Apply(options, "admin", ctx.Connection.RemoteIpAddress?.ToString());
                await WriteJsonAsync(ctx, StatusCodes.Status200OK, result, GatewayJsonContext.Default.ConfigChangeResult);
            }
            catch (GatewayConfigException ex)
            {
                logger.LogWarning("Configuration submitted by {ClientIp} was rejected: {Reason}", ctx.Connection.RemoteIpAddress, ex.Message);
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Could not write the configuration change log; the change was not applied");
                await WriteErrorAsync(ctx, StatusCodes.Status500InternalServerError, "Could not write the change log; the configuration was not applied.");
            }
        };

        app.MapGet(ConfigPath, get);
        app.MapPut(ConfigPath, put);
    }

    private static bool IsAuthorized(HttpContext ctx, byte[] keyHash, ILogger logger)
    {
        var provided = ctx.Request.Headers[ApiKeyHeader].ToString();
        if (provided.Length > 0 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(provided)), keyHash))
        {
            return true;
        }

        logger.LogWarning("Unauthorized admin request {Method} {Path} from {ClientIp}",
            ctx.Request.Method, ctx.Request.Path, ctx.Connection.RemoteIpAddress);
        return false;
    }

    private static Task WriteErrorAsync(HttpContext ctx, int statusCode, string error) =>
        WriteJsonAsync(ctx, statusCode, new AdminError { Error = error }, GatewayJsonContext.Default.AdminError);

    private static async Task WriteJsonAsync<T>(HttpContext ctx, int statusCode, T value, JsonTypeInfo<T> typeInfo)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            JsonSerializer.Serialize(writer, value, typeInfo);
        }

        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength = buffer.Length;
        await ctx.Response.Body.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), ctx.RequestAborted);
    }
}
