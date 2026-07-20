using Microsoft.AspNetCore.Routing;

namespace Rankoon.Api;

public sealed class NonApiPathRouteConstraint : IRouteConstraint
{
    public bool Match(
        HttpContext? httpContext,
        IRouter? route,
        string routeKey,
        RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        var path = values[routeKey]?.ToString()?.TrimStart('/');
        return !string.Equals(path, "api", StringComparison.OrdinalIgnoreCase) &&
            !(path?.StartsWith("api/", StringComparison.OrdinalIgnoreCase) ?? false) &&
            !Path.HasExtension(path);
    }
}
