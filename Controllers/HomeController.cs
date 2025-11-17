using Microsoft.AspNetCore.Mvc;

namespace BarangayProject.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index() => View();
    }
}
