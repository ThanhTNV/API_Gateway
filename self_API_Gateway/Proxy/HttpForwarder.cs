using System.Collections.Frozen;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using self_API_Gateway.Backends;

namespace self_API_Gateway.Proxy;

public sealed class HttpForwarder(HttpMessageInvoker invoker, ILogger<HttpForwarder> logger)
{
    private const string XForwardedFor = "X-Forwarded-For";
    private const string XForwardedProto = "X-Forwarded-Proto";
    private const string XForwardedHost = "X-Forwarded-Host";

    private static readonly FrozenSet<string> HopByHopHeaders = FrozenSet.ToFrozenSet(
        [HeaderNames.Connection, HeaderNames.KeepAlive, HeaderNames.TransferEncoding, HeaderNames.Upgrade, HeaderNames.TE, HeaderNames.Trailer],
        StringComparer.OrdinalIgnoreCase);

    // One shared handler for every destination: connections are pooled per host.
    public static HttpMessageInvoker CreateInvoker() => new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        // Recycle connections so DNS changes are picked up.
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(10)
    });

    public async Task ForwardAsync(HttpContext ctx, ClusterState cluster, DestinationState destination, PathString pathPrefix)
    {
        var aborted = ctx.RequestAborted;
        try
        {
            using var request = CreateRequest(ctx, destination, pathPrefix);

            HttpResponseMessage response;
            try
            {
                // HttpMessageInvoker never buffers the response: this returns once the headers are read.
                response = await invoker.SendAsync(request, aborted);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                // The client going away is not the destination's fault.
                if (aborted.IsCancellationRequested) return;

                cluster.ReportFailure(destination);
                logger.LogWarning(ex, "Forwarding {Method} {Path} to {Destination} in cluster {Cluster} failed",
                    ctx.Request.Method, ctx.Request.Path, destination.Address, cluster.Id);
                ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            using (response)
            {
                if ((int)response.StatusCode >= 500)
                {
                    logger.LogWarning("{Destination} in cluster {Cluster} answered {StatusCode} for {Method} {Path}",
                        destination.Address, cluster.Id, (int)response.StatusCode, ctx.Request.Method, ctx.Request.Path);
                }

                ctx.Response.StatusCode = (int)response.StatusCode;
                CopyResponseHeaders(response.Headers, ctx.Response.Headers);
                CopyResponseHeaders(response.Content.Headers, ctx.Response.Headers);

                try
                {
                    await response.Content.CopyToAsync(ctx.Response.Body, aborted);
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
                {
                    // The status line is already sent, so all that's left is to cut the connection.
                    if (!aborted.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Response from {Destination} in cluster {Cluster} was interrupted", destination.Address, cluster.Id);
                        ctx.Abort();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Anything that isn't a network failure is a gateway bug.
            logger.LogError(ex, "Unexpected error forwarding {Method} {Path} to {Destination} in cluster {Cluster}",
                ctx.Request.Method, ctx.Request.Path, destination.Address, cluster.Id);
            if (ctx.Response.HasStarted)
            {
                ctx.Abort();
            }
            else
            {
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        }
    }

    private static HttpRequestMessage CreateRequest(HttpContext ctx, DestinationState destination, PathString pathPrefix)
    {
        var source = ctx.Request;
        if (!source.Path.StartsWithSegments(pathPrefix, out var remaining))
        {
            remaining = source.Path;
        }

        var request = new HttpRequestMessage(
            new HttpMethod(source.Method),
            destination.BaseUrl + remaining.ToUriComponent() + source.QueryString.ToUriComponent());

        var hasBody = ctx.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody ?? (source.ContentLength > 0);
        if (hasBody)
        {
            request.Content = new StreamContent(source.Body);
        }

        foreach (var (name, values) in source.Headers)
        {
            if (IsHopByHop(name) ||
                name.StartsWith(':') || // HTTP/2 pseudo-headers
                name.Equals(HeaderNames.Host, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(XForwardedProto, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(XForwardedHost, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Content headers (Content-Type, Content-Length, ...) are rejected by the request collection and belong on the content.
            var array = values.ToArray();
            if (!request.Headers.TryAddWithoutValidation(name, array))
            {
                request.Content?.Headers.TryAddWithoutValidation(name, array);
            }
        }

        // Appends to any X-Forwarded-For chain copied above.
        if (ctx.Connection.RemoteIpAddress is { } remoteIp)
        {
            request.Headers.TryAddWithoutValidation(XForwardedFor, remoteIp.ToString());
        }
        request.Headers.TryAddWithoutValidation(XForwardedProto, source.Scheme);
        if (source.Host.HasValue)
        {
            request.Headers.TryAddWithoutValidation(XForwardedHost, source.Host.Value);
        }

        return request;
    }

    private static void CopyResponseHeaders(HttpHeaders source, IHeaderDictionary destination)
    {
        foreach (var (name, values) in source.NonValidated)
        {
            if (IsHopByHop(name)) continue;

            if (values.Count == 1)
            {
                destination[name] = values.ToString();
                continue;
            }

            // Keep multi-value headers such as Set-Cookie as separate lines.
            var array = new string[values.Count];
            var i = 0;
            foreach (var value in values) array[i++] = value;
            destination[name] = new StringValues(array);
        }
    }

    private static bool IsHopByHop(string name) =>
        HopByHopHeaders.Contains(name) || name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase);
}
