using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Rankoon.Controllers;

namespace Rankoon.Data.Auth;

public interface IBrowserSessionService
{
    CsrfBootstrapResponse BootstrapCsrf(HttpContext context);
}

public sealed class BrowserSessionService(IOptions<AuthCookieOptions> options, IAntiforgery antiforgery) : IAuthCookieService, IOAuthCallbackCookieService, IBrowserSessionService
{
    private readonly AuthCookieOptions options = options.Value;

    public string? GetRefreshToken(HttpRequest request) => request.Cookies.TryGetValue(options.RefreshCookieName, out var token) ? token : null;

    public void SetAuthCookies(HttpResponse response, TokenResponse tokens)
    {
        response.Cookies.Append(options.AccessCookieName, tokens.AccessToken, CookieOptions(tokens.ExpiresAt));
        response.Cookies.Append(options.RefreshCookieName, tokens.RefreshToken, CookieOptions(null));
    }

    public Task SetTokensAsync(TokenResponse tokens, HttpResponse response, CancellationToken cancellationToken = default)
    {
        SetAuthCookies(response, tokens);
        return Task.CompletedTask;
    }

    public void DeleteAuthCookies(HttpResponse response)
    {
        response.Cookies.Delete(options.AccessCookieName, CookieOptions(null));
        response.Cookies.Delete(options.RefreshCookieName, CookieOptions(null));
    }

    public CsrfBootstrapResponse BootstrapCsrf(HttpContext context) => new() { Token = antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty };

    private CookieOptions CookieOptions(DateTime? expiresAt) => new()
    {
        HttpOnly = true,
        Secure = options.Secure,
        SameSite = options.SameSite,
        Path = "/",
        IsEssential = true,
        Expires = expiresAt
    };
}

public sealed class CsrfValidationMiddleware(RequestDelegate next, IAntiforgery antiforgery, IOptions<AuthCookieOptions> options)
{
    private readonly AuthCookieOptions options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (RequiresValidation(context.Request) && HasBrowserSession(context.Request))
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException)
            {
                await Rankoon.Api.ApiErrorFactory.WriteAsync(context, "auth.forbidden");
                return;
            }
        }
        await next(context);
    }

    private static bool RequiresValidation(HttpRequest request) => request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method) && !HttpMethods.IsTrace(request.Method);
    private bool HasBrowserSession(HttpRequest request) => request.Cookies.ContainsKey(options.AccessCookieName) || request.Cookies.ContainsKey(options.RefreshCookieName);
}
