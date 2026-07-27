using Microsoft.AspNetCore.Mvc;
using Rankoon.Data.Auth;

namespace Rankoon.Controllers;

[ApiController]
[Route("api/auth/csrf")]
public sealed class CsrfController(IBrowserSessionService sessions) : ControllerBase
{
    [HttpGet]
    public ActionResult<CsrfBootstrapResponse> Bootstrap() => Ok(sessions.BootstrapCsrf(HttpContext));
}
