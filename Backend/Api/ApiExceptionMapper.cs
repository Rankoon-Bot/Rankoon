using System.Net;
using System.Net.Sockets;
using MongoDB.Driver;
using DiscordHttpException = Discord.Net.HttpException;

namespace Rankoon.Api;

/// <summary>Maps known operational failures to safe, actionable API errors, never exception text.</summary>
public static class ApiExceptionMapper
{
    public static ApiErrorDefinition Map(Exception? exception)
    {
        var causes = Causes(exception).ToArray();
        // A wrapper (including InvalidOperationException and AggregateException) must not hide Discord's cause.
        var discord = causes.OfType<DiscordHttpException>().FirstOrDefault();
        if (discord != null) return ApiErrorCatalog.Get(DiscordKey(discord));
        if (causes.Any(cause => cause is TimeoutException or OperationCanceledException or MongoExecutionTimeoutException))
            return ApiErrorCatalog.Get("request.timeout");
        if (causes.Any(cause => cause is HttpRequestException or SocketException or MongoConnectionException))
            return ApiErrorCatalog.Get("service.unavailable");
        return ApiErrorCatalog.Get("server.internal");
    }

    public static bool IsClientCancellation(Exception? exception, CancellationToken requestAborted) =>
        requestAborted.IsCancellationRequested && Causes(exception).Any(cause => cause is OperationCanceledException);

    public static Task WriteAsync(HttpContext context, Exception? exception) =>
        ApiErrorFactory.WriteAsync(context, Map(exception).Key);

    private static string DiscordKey(DiscordHttpException exception) => (int?)exception.DiscordCode switch
    {
        50001 => "discord.missingAccess",
        50013 => "discord.missingPermissions",
        10003 or 10004 or 10007 or 10008 or 10011 or 10014 => "discord.resourceUnavailable",
        30005 or 30007 or 30008 or 30013 or 30030 => "discord.limitReached",
        50035 or 50024 or 50008 or 30052 => "discord.invalidRequest",
        40001 or 50014 or 50027 => "discord.authenticationFailed",
        20028 or 20029 or 40062 => "discord.rateLimited",
        _ => exception.HttpCode switch
        {
            HttpStatusCode.Forbidden => "discord.missingPermissions",
            HttpStatusCode.NotFound => "discord.resourceUnavailable",
            HttpStatusCode.Unauthorized => "discord.authenticationFailed",
            HttpStatusCode.BadRequest => "discord.invalidRequest",
            HttpStatusCode.TooManyRequests => "discord.rateLimited",
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "request.timeout",
            >= HttpStatusCode.InternalServerError => "discord.unavailable",
            _ => "discord.failed"
        }
    };

    private static IEnumerable<Exception> Causes(Exception? exception)
    {
        if (exception == null) yield break;
        var pending = new Queue<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Enqueue(exception);
        while (pending.Count > 0 && seen.Count < 64)
        {
            var cause = pending.Dequeue();
            if (!seen.Add(cause)) continue;
            yield return cause;
            if (cause is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Enqueue(inner);
            else if (cause.InnerException != null) pending.Enqueue(cause.InnerException);
        }
    }
}
