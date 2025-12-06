using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

// Controller: HomeController — handles web requests for home
public class HomeController : Controller
{
    [Route("Home/Error")]
    [AllowAnonymous]
    // Short: Error — IActionResult action
    public IActionResult Error()
    {
        var exFeature = HttpContext.Features.Get<IExceptionHandlerFeature>();
        ViewBag.ErrorMessage = exFeature?.Error?.Message;
        ViewBag.StackTrace = exFeature?.Error?.StackTrace;
        Response.StatusCode = 500;
        return View();
    }
}