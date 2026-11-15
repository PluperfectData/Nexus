using Microsoft.AspNetCore.Mvc;

namespace Nexus.Service.Controllers;

/// <summary>Answers whether the service is up.</summary>
[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok" });
}
