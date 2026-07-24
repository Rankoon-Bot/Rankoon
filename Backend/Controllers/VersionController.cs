using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Rankoon.Controllers;

[ApiController]
[Route("api/info")]
[Route("api/version")]
public sealed class VersionController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var assembly = Assembly.GetEntryAssembly()!;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        return Ok(new { buildVersion = version });
    }
}
