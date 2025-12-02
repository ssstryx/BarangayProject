using BarangayProject.Data;
using BarangayProject.Models;
using BarangayProject.Models.BhwModel;
using BarangayProject.Models.BnsModel;
using BarangayProject.Models.AdminModel;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BarangayProject.Controllers
{
    public class BnsController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;

        public BnsController(
            ApplicationDbContext db,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager)
        {
            _db = db;
            _userManager = userManager;
            _signInManager = signInManager;
        }

        // Index: dashboard (returns BnsDashboardVm)
        public async Task<IActionResult> Index()
        {
            var totalHouseholds = await _db.Households.AsNoTracking().Where(h => !h.IsArchived).CountAsync();
            var totalResidents = await _db.Residents.AsNoTracking().CountAsync();
            var totalFemale = await _db.Residents.AsNoTracking().Where(r => r.Sex == "Female").CountAsync();
            var totalMale = await _db.Residents.AsNoTracking().Where(r => r.Sex == "Male").CountAsync();

            var labels = new List<string>();
            var values = new List<int>();

            var now = DateTime.UtcNow.Date;

            for (int i = 5; i >= 0; i--)
            {
                var month = now.AddMonths(-i);
                var monthStart = new DateTime(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var monthEnd = monthStart.AddMonths(1).AddTicks(-1);

                // count residents created within this month
                var count = await _db.Residents
                                     .AsNoTracking()
                                     .Where(r => r.CreatedAt.HasValue && r.CreatedAt.Value >= monthStart && r.CreatedAt.Value <= monthEnd)
                                     .CountAsync();

                labels.Add(monthStart.ToString("MMM yyyy"));
                values.Add(count);
            }

            var vm = new BnsDashboardVm
            {
                TotalHouseholds = totalHouseholds,
                TotalPopulation = totalResidents,
                TotalFemale = totalFemale,
                TotalMale = totalMale,
                TrendLabels = labels,
                TrendValues = values,
            };

            return View(vm);
        }

        // Households list (read-only for BNS)
        public async Task<IActionResult> Households(string search, int? sitioId)
        {
            var query = _db.Households
                       .AsNoTracking()
                       .Include(h => h.Sitio)
                       .Where(h => !h.IsArchived)
                       .AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(h => EF.Functions.Like(h.FamilyHead, $"%{search}%"));

            if (sitioId.HasValue)
                query = query.Where(h => h.SitioId == sitioId.Value);

            ViewBag.Sitios = await _db.Sitios
                                      .AsNoTracking()
                                      .OrderBy(s => s.Name)
                                      .Select(s => new { s.Id, s.Name })
                                      .ToListAsync();

            ViewBag.SelectedSitioId = sitioId?.ToString() ?? "";
            ViewBag.CurrentSearch = search ?? "";

            var list = await query.OrderBy(h => h.Id).ToListAsync();
            return View(list);
        }

        // Household detail (read-only)
        public async Task<IActionResult> ViewHousehold(int id)
        {
            var h = await _db.Households
                             .AsNoTracking()
                             .Include(hh => hh.Sitio)
                             .Include(hh => hh.Residents)
                             .FirstOrDefaultAsync(hh => hh.Id == id);

            if (h == null) return NotFound();

            ViewBag.From = HttpContext.Request.Query["from"].ToString() ?? "";

            return View(h);
        }
        public async Task<IActionResult> Residents(string q, int? sitioId)
        {
            var baseQuery = _db.Residents
                               .AsNoTracking()
                               .Include(r => r.Household)
                                   .ThenInclude(h => h.Sitio)
                               .AsQueryable();

            if (sitioId.HasValue)
            {
                baseQuery = baseQuery.Where(r => r.Household != null && r.Household.SitioId == sitioId.Value);
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = $"%{q}%";
                baseQuery = baseQuery.Where(r =>
                    EF.Functions.Like(r.FirstName ?? "", term) ||
                    EF.Functions.Like(r.MiddleName ?? "", term) ||
                    EF.Functions.Like(r.LastName ?? "", term) ||
                    EF.Functions.Like(r.Occupation ?? "", term) ||
                    EF.Functions.Like(r.Education ?? "", term));
            }

            var list = await baseQuery
                .OrderBy(r => r.Id)
                .Select(r => new ResidentListVm
                {
                    Id = r.Id,
                    HouseholdId = r.HouseholdId,
                    Name = (
                        ((r.FirstName ?? "") + " " +
                         (r.MiddleName ?? "") + " " +
                         (r.LastName ?? "") +
                         (string.IsNullOrWhiteSpace(r.Extension) ? "" : " " + r.Extension)
                        ).Trim()
                    ),
                    Sex = r.Sex ?? "",
                    Occupation = r.Occupation ?? "",
                    Education = r.Education ?? "",
                    DateOfBirth = r.DateOfBirth,
                    Age = r.DateOfBirth.HasValue ? (int?)CalculateAge(r.DateOfBirth.Value) : r.Age
                })
                .ToListAsync();

            ViewBag.Sitios = await _db.Sitios
                                      .AsNoTracking()
                                      .OrderBy(s => s.Name)
                                      .Select(s => new { s.Id, s.Name })
                                      .ToListAsync();

            ViewBag.SelectedSitioId = sitioId?.ToString() ?? "";
            ViewBag.SearchQuery = q ?? "";

            return View(list);
        }

        // Resident detail (read-only)
        public async Task<IActionResult> ViewResident(int id)
        {
            var r = await _db.Residents
                             .AsNoTracking()
                             .Include(x => x.Household)
                                .ThenInclude(h => h.Sitio)
                             .FirstOrDefaultAsync(x => x.Id == id);

            if (r == null) return NotFound();
            return View(r);
        }

        // -----------------------
        // Archived lists (BNS view-only)
        // -----------------------

        // GET: /Bns/ArchivedResidents?q=...&sitioId=...
        public async Task<IActionResult> ArchivedResidents(string q, int? sitioId)
        {
            var baseQuery = _db.Residents
                               .AsNoTracking()
                               .Include(r => r.Household)
                                   .ThenInclude(h => h.Sitio)
                               .Where(r => r.IsArchived == true) // archived only
                               .AsQueryable();

            if (sitioId.HasValue)
            {
                baseQuery = baseQuery.Where(r => r.Household != null && r.Household.SitioId == sitioId.Value);
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = $"%{q}%";
                baseQuery = baseQuery.Where(r =>
                    EF.Functions.Like(r.FirstName ?? "", term) ||
                    EF.Functions.Like(r.MiddleName ?? "", term) ||
                    EF.Functions.Like(r.LastName ?? "", term) ||
                    EF.Functions.Like(r.Occupation ?? "", term) ||
                    EF.Functions.Like(r.Education ?? "", term));
            }

            var list = await baseQuery
                .OrderBy(r => r.Id)
                .Select(r => new ResidentListVm
                {
                    Id = r.Id,
                    HouseholdId = r.HouseholdId,
                    Name = (
                        ((r.FirstName ?? "") + " " +
                         (r.MiddleName ?? "") + " " +
                         (r.LastName ?? "") +
                         (string.IsNullOrWhiteSpace(r.Extension) ? "" : " " + r.Extension)
                        ).Trim()
                    ),
                    Sex = r.Sex ?? "",
                    Occupation = r.Occupation ?? "",
                    Education = r.Education ?? "",
                    DateOfBirth = r.DateOfBirth,
                    Age = r.DateOfBirth.HasValue ? (int?)CalculateAge(r.DateOfBirth.Value) : r.Age
                })
                .ToListAsync();

            ViewBag.Sitios = await _db.Sitios
                                      .AsNoTracking()
                                      .OrderBy(s => s.Name)
                                      .Select(s => new { s.Id, s.Name })
                                      .ToListAsync();

            ViewBag.SelectedSitioId = sitioId?.ToString() ?? "";
            ViewBag.SearchQuery = q ?? "";

            // render view located at Views/Bns/ArchivedResidents.cshtml
            return View("ArchivedResidents", list);
        }

        // GET: /Bns/ArchivedHouseholds?sitioId=...
        public async Task<IActionResult> ArchivedHouseholds(int? sitioId)
        {
            var query = _db.Households
                           .AsNoTracking()
                           .Include(h => h.Sitio)
                           .Where(h => h.IsArchived == true)
                           .AsQueryable();

            if (sitioId.HasValue)
                query = query.Where(h => h.SitioId == sitioId.Value);

            var list = await query.OrderBy(h => h.Id).ToListAsync();

            ViewBag.Sitios = await _db.Sitios
                                      .AsNoTracking()
                                      .OrderBy(s => s.Name)
                                      .Select(s => new { s.Id, s.Name })
                                      .ToListAsync();

            ViewBag.SelectedSitioId = sitioId?.ToString() ?? "";

            // render view located at Views/Bns/ArchivedHouseholds.cshtml
            return View("ArchivedHouseholds", list);
        }

        // -----------------------
        // Reports: view + data endpoints
        // -----------------------

        // GET: /Bns/Reports
        public async Task<IActionResult> Reports()
        {
            // Sitio filter for the reports page dropdown
            ViewBag.Sitios = await _db.Sitios
                                      .AsNoTracking()
                                      .OrderBy(s => s.Name)
                                      .Select(s => new { s.Id, s.Name })
                                      .ToListAsync();

            ViewBag.SelectedSitioId = "";
            // return default view (Views/Bns/Reports.cshtml)
            return View();
        }
        [HttpGet]
        public async Task<IActionResult> GetReportData(string reportType = "households", string startDate = null, string endDate = null, int? sitioId = null)
        {
            // Parse dates (inclusive)
            DateTime? start = null, end = null;
            if (DateTime.TryParse(startDate, out var sd)) start = sd.Date;
            if (DateTime.TryParse(endDate, out var ed)) end = ed.Date.AddDays(1).AddTicks(-1);

            reportType = (reportType ?? "households").ToLowerInvariant();

            var columns = new List<string>();
            var rows = new List<Dictionary<string, object>>();

            if (reportType == "households" || reportType == "archived-households")
            {
                var wantArchived = reportType == "archived-households";
                var query = _db.Households.AsNoTracking().Include(h => h.Sitio).AsQueryable();

                // If IsArchived is nullable bool in your model, comparing directly is fine; adjust if needed.
                query = query.Where(h => (h.IsArchived == wantArchived));

                if (sitioId.HasValue)
                    query = query.Where(h => h.SitioId == sitioId.Value);

                if (start.HasValue)
                    query = query.Where(h => h.CreatedAt >= start.Value);

                if (end.HasValue)
                    query = query.Where(h => h.CreatedAt <= end.Value);

                var list = await query.OrderBy(h => h.Id).ToListAsync();

                columns.AddRange(new[] { "HouseholdId", "FamilyHead", "Sitio", "CreatedAt", "IsArchived" });

                foreach (var h in list)
                {
                    rows.Add(new Dictionary<string, object>
                    {
                        ["HouseholdId"] = h.Id,
                        ["FamilyHead"] = h.FamilyHead,
                        ["Sitio"] = h.Sitio?.Name ?? (h.SitioId?.ToString() ?? "-"),
                        ["CreatedAt"] = (h.CreatedAt == default(DateTime) ? "" : h.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd")),
                        ["IsArchived"] = h.IsArchived
                    });
                }
            }
            else if (reportType == "residents" || reportType == "archived-residents")
            {
                var wantArchived = reportType == "archived-residents";
                var query = _db.Residents.AsNoTracking()
                            .Include(r => r.Household)
                                .ThenInclude(h => h.Sitio)
                            .AsQueryable();

                if (wantArchived)
                    query = query.Where(r => r.IsArchived == true);
                else
                    query = query.Where(r => r.IsArchived != true);

                if (sitioId.HasValue)
                    query = query.Where(r => r.Household != null && r.Household.SitioId == sitioId.Value);

                if (start.HasValue)
                    query = query.Where(r => r.CreatedAt.HasValue && r.CreatedAt.Value >= start.Value);

                if (end.HasValue)
                    query = query.Where(r => r.CreatedAt.HasValue && r.CreatedAt.Value <= end.Value);

                var list = await query.OrderBy(r => r.Id).ToListAsync();

                columns.AddRange(new[] { "ResidentId", "HouseholdId", "Name", "Sex", "Occupation", "Education", "DateOfBirth", "Age", "IsArchived" });

                foreach (var r in list)
                {
                    var name = $"{r.FirstName} {r.MiddleName} {r.LastName} {(string.IsNullOrWhiteSpace(r.Extension) ? "" : r.Extension)}".Replace("  ", " ").Trim();
                    var age = r.DateOfBirth.HasValue
                        ? (DateTime.UtcNow.Date.Year - r.DateOfBirth.Value.Date.Year - (DateTime.UtcNow.Date < r.DateOfBirth.Value.Date.AddYears(DateTime.UtcNow.Date.Year - r.DateOfBirth.Value.Date.Year) ? 1 : 0))
                        : r.Age;
                    rows.Add(new Dictionary<string, object>
                    {
                        ["ResidentId"] = r.Id,
                        ["HouseholdId"] = r.HouseholdId,
                        ["Name"] = name,
                        ["Sex"] = r.Sex ?? "",
                        ["Occupation"] = r.Occupation ?? "",
                        ["Education"] = r.Education ?? "",
                        ["DateOfBirth"] = r.DateOfBirth.HasValue ? r.DateOfBirth.Value.ToString("yyyy-MM-dd") : "",
                        ["Age"] = age,
                        ["IsArchived"] = r.IsArchived == true
                    });
                }
            }
            else
            {
                return BadRequest(new { error = "Unknown reportType" });
            }

            return Json(new { columns, rows });
        }

        // Simple CSV export (re-uses same filtering as GetReportData)
        [HttpGet]
        public async Task<IActionResult> ExportCsv(string reportType = "households", string startDate = null, string endDate = null, int? sitioId = null)
        {
            var dataResult = await GetReportPayload(reportType, startDate, endDate, sitioId);
            if (dataResult == null) return BadRequest();

            var columns = dataResult.Value.columns;
            var rows = dataResult.Value.rows;

            var sb = new StringBuilder();
            // header
            sb.AppendLine(string.Join(",", columns.Select(c => CsvEscape(c))));
            // rows
            foreach (var r in rows)
            {
                var parts = columns.Select(c =>
                {
                    r.TryGetValue(c, out var v);
                    return CsvEscape(v?.ToString() ?? "");
                });
                sb.AppendLine(string.Join(",", parts));
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            return File(bytes, "text/csv; charset=utf-8", $"{reportType}_{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
        }

        // Simple Excel export: return CSV with .xlsx name (quick fallback)
        [HttpGet]
        public async Task<IActionResult> ExportExcel(string reportType = "households", string startDate = null, string endDate = null, int? sitioId = null)
        {
            // For now return same CSV but with .xlsx filename (replace later with proper Excel generation)
            return await ExportCsv(reportType, startDate, endDate, sitioId);
        }

        // PDF placeholder
        [HttpGet]
        public IActionResult ExportPdf(string reportType = "households", string startDate = null, string endDate = null, int? sitioId = null)
        {
            // implement with a PDF library later (DinkToPdf, wkhtmltopdf, etc.)
            return StatusCode(501, "PDF export not implemented yet.");
        }

        // -----------------------
        // Settings (change password)
        // -----------------------

        // GET: /Bns/Settings
        [HttpGet]
        public IActionResult Settings()
        {
            var vm = new ChangePasswordVm();
            return View(vm);
        }

        // POST: /Bns/Settings
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Settings(ChangePasswordVm model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                TempData["ErrorMessage"] = "Unable to find the current user.";
                return RedirectToAction(nameof(Index));
            }

            var result = await _userManager.ChangePasswordAsync(user, model.CurrentPassword ?? "", model.NewPassword ?? "");
            if (!result.Succeeded)
            {
                foreach (var e in result.Errors)
                    ModelState.AddModelError(string.Empty, e.Description);

                return View(model);
            }

            // refresh sign-in so cookie/security stamp is updated
            await _signInManager.RefreshSignInAsync(user);

            TempData["SuccessMessage"] = "Password changed successfully.";
            return RedirectToAction(nameof(Settings));
        }

        // -----------------------
        // Helpers
        // -----------------------

        // Small helper to prepare the same payload used by GetReportData for file exports
        private async Task<(List<string> columns, List<Dictionary<string, object>> rows)?> GetReportPayload(string reportType, string startDate, string endDate, int? sitioId)
        {
            // basically reuse logic from GetReportData but return data instead of IActionResult
            DateTime? start = null, end = null;
            if (DateTime.TryParse(startDate, out var sd)) start = sd.Date;
            if (DateTime.TryParse(endDate, out var ed)) end = ed.Date.AddDays(1).AddTicks(-1);

            reportType = (reportType ?? "households").ToLowerInvariant();

            var columns = new List<string>();
            var rows = new List<Dictionary<string, object>>();

            if (reportType == "households" || reportType == "archived-households")
            {
                var wantArchived = reportType == "archived-households";
                var query = _db.Households.AsNoTracking().Include(h => h.Sitio).AsQueryable();
                query = query.Where(h => (h.IsArchived == wantArchived));

                if (sitioId.HasValue) query = query.Where(h => h.SitioId == sitioId.Value);
                if (start.HasValue) query = query.Where(h => h.CreatedAt >= start.Value);
                if (end.HasValue) query = query.Where(h => h.CreatedAt <= end.Value);

                var list = await query.OrderBy(h => h.Id).ToListAsync();
                columns.AddRange(new[] { "HouseholdId", "FamilyHead", "Sitio", "CreatedAt", "IsArchived" });
                foreach (var h in list)
                {
                    rows.Add(new Dictionary<string, object>
                    {
                        ["HouseholdId"] = h.Id,
                        ["FamilyHead"] = h.FamilyHead,
                        ["Sitio"] = h.Sitio?.Name ?? (h.SitioId?.ToString() ?? "-"),
                        ["CreatedAt"] = (h.CreatedAt == default(DateTime) ? "" : h.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd")),
                        ["IsArchived"] = h.IsArchived
                    });
                }
            }
            else if (reportType == "residents" || reportType == "archived-residents")
            {
                var wantArchived = reportType == "archived-residents";
                var query = _db.Residents.AsNoTracking().Include(r => r.Household).ThenInclude(h => h.Sitio).AsQueryable();

                if (wantArchived) query = query.Where(r => r.IsArchived == true);
                else query = query.Where(r => r.IsArchived != true);

                if (sitioId.HasValue) query = query.Where(r => r.Household != null && r.Household.SitioId == sitioId.Value);
                if (start.HasValue) query = query.Where(r => r.CreatedAt.HasValue && r.CreatedAt.Value >= start.Value);
                if (end.HasValue) query = query.Where(r => r.CreatedAt.HasValue && r.CreatedAt.Value <= end.Value);

                var list = await query.OrderBy(r => r.Id).ToListAsync();
                columns.AddRange(new[] { "ResidentId", "HouseholdId", "Name", "Sex", "Occupation", "Education", "DateOfBirth", "Age", "IsArchived" });

                foreach (var r in list)
                {
                    var name = $"{r.FirstName} {r.MiddleName} {r.LastName} {(string.IsNullOrWhiteSpace(r.Extension) ? "" : r.Extension)}".Replace("  ", " ").Trim();
                    var age = r.DateOfBirth.HasValue ? (DateTime.UtcNow.Date.Year - r.DateOfBirth.Value.Date.Year - (DateTime.UtcNow.Date < r.DateOfBirth.Value.Date.AddYears(DateTime.UtcNow.Date.Year - r.DateOfBirth.Value.Date.Year) ? 1 : 0)) : r.Age;
                    rows.Add(new Dictionary<string, object>
                    {
                        ["ResidentId"] = r.Id,
                        ["HouseholdId"] = r.HouseholdId,
                        ["Name"] = name,
                        ["Sex"] = r.Sex ?? "",
                        ["Occupation"] = r.Occupation ?? "",
                        ["Education"] = r.Education ?? "",
                        ["DateOfBirth"] = r.DateOfBirth.HasValue ? r.DateOfBirth.Value.ToString("yyyy-MM-dd") : "",
                        ["Age"] = age,
                        ["IsArchived"] = r.IsArchived == true
                    });
                }
            }
            else
            {
                return null;
            }

            return (columns, rows);
        }

        private static string CsvEscape(string input)
        {
            if (input == null) return "";
            var needsQuote = input.Contains(",") || input.Contains("\"") || input.Contains("\n") || input.Contains("\r");
            var s = input.Replace("\"", "\"\"");
            return needsQuote ? "\"" + s + "\"" : s;
        }

        // helper: calculate age from DOB (UTC-aware)
        private static int CalculateAge(DateTime dobUtc)
        {
            var today = DateTime.UtcNow.Date;
            var dob = dobUtc.Date;
            var age = today.Year - dob.Year;
            if (today < dob.AddYears(age)) age--;
            return age;
        }
    }
}
