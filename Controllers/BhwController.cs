// Controllers/BhwController.cs
using BarangayProject.Data;
using BarangayProject.Models;
using BarangayProject.Models.AdminModel;
using BarangayProject.Models.BhwModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace BarangayProject.Controllers
{
    [Authorize(Roles = "BHW")]
    public class BhwController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApplicationDbContext _db;
        private readonly ILogger<BhwController> _logger;

        public BhwController(UserManager<ApplicationUser> userManager, ApplicationDbContext db, ILogger<BhwController> logger)
        {
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private int? ComputeAge(DateTime? dob)
        {
            if (dob == null) return null;
            var today = DateTime.UtcNow.Date;
            var b = dob.Value.Date;
            if (b > today) return 0;
            int age = today.Year - b.Year;
            if (b > today.AddYears(-age)) age--;
            return age;
        }

        private IQueryable<object> GetQueryableIfExists(params string[] candidateNames)
        {
            var ctxType = _db.GetType();
            foreach (var name in candidateNames)
            {
                var prop = ctxType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop == null) continue;

                var val = prop.GetValue(_db);
                if (val is IQueryable q) return q.Cast<object>();
            }

            foreach (var prop in ctxType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var val = prop.GetValue(_db);
                if (val is IQueryable q)
                {
                    var elemName = q.ElementType.Name.ToLowerInvariant();
                    foreach (var candidate in candidateNames)
                    {
                        if (elemName == candidate.ToLowerInvariant() || elemName == candidate.ToLowerInvariant().TrimEnd('s'))
                            return q.Cast<object>();
                    }
                }
            }

            return null;
        }

        private int CountQueryable(IQueryable<object> source)
        {
            if (source == null) return 0;
            try
            {
                var elementType = source.ElementType;
                var countMethod = typeof(Queryable).GetMethods()
                    .First(m => m.Name == "Count" && m.GetParameters().Length == 1)
                    .MakeGenericMethod(elementType);

                var result = countMethod.Invoke(null, new object[] { source.Expression });
                return Convert.ToInt32(result);
            }
            catch { return 0; }
        }

        private int CountWhereStringEquals(IQueryable<object> source, string propertyName, string wantedValue)
        {
            if (source == null) return 0;
            try
            {
                var elementType = source.ElementType;
                var prop = elementType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop == null || prop.PropertyType != typeof(string)) return 0;

                var parameter = System.Linq.Expressions.Expression.Parameter(elementType, "x");
                var member = System.Linq.Expressions.Expression.Property(parameter, prop);
                var constant = System.Linq.Expressions.Expression.Constant(wantedValue, typeof(string));
                var equal = System.Linq.Expressions.Expression.Equal(member, constant);
                var lambda = System.Linq.Expressions.Expression.Lambda(equal, parameter);

                var whereCall = System.Linq.Expressions.Expression.Call(typeof(Queryable), "Where",
                    new Type[] { elementType }, source.Expression, lambda);

                var whereQuery = source.Provider.CreateQuery(whereCall);

                var countMethod = typeof(Queryable).GetMethods()
                    .First(m => m.Name == "Count" && m.GetParameters().Length == 1)
                    .MakeGenericMethod(elementType);

                var cnt = countMethod.Invoke(null, new object[] { whereQuery });
                return Convert.ToInt32(cnt);
            }
            catch { return 0; }
        }

        private int CountWhereBoolTrue(IQueryable<object> source, string propertyName)
        {
            if (source == null) return 0;
            try
            {
                var elementType = source.ElementType;
                var prop = elementType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop == null || (prop.PropertyType != typeof(bool) && prop.PropertyType != typeof(bool?))) return 0;

                var parameter = System.Linq.Expressions.Expression.Parameter(elementType, "x");
                var member = System.Linq.Expressions.Expression.Property(parameter, prop);
                var constant = System.Linq.Expressions.Expression.Constant(true, member.Type);
                var equal = System.Linq.Expressions.Expression.Equal(member, constant);
                var lambda = System.Linq.Expressions.Expression.Lambda(equal, parameter);

                var whereCall = System.Linq.Expressions.Expression.Call(typeof(Queryable), "Where", new Type[] { elementType }, source.Expression, lambda);
                var whereQuery = source.Provider.CreateQuery(whereCall);

                var countMethod = typeof(Queryable).GetMethods()
                    .First(m => m.Name == "Count" && m.GetParameters().Length == 1)
                    .MakeGenericMethod(elementType);

                var cnt = countMethod.Invoke(null, new object[] { whereQuery });
                return Convert.ToInt32(cnt);
            }
            catch { return 0; }
        }

        #region Sitio helpers
        private async Task<Sitio?> GetAssignedSitioAsync()
        {
            var userId = _userManager.GetUserId(User);
            if (string.IsNullOrEmpty(userId)) return null;

            try
            {
                // 1) try direct AssignedBhwId field on Sitios (legacy)
                try
                {
                    var sitioDirect = await _db.Sitios.AsNoTracking().FirstOrDefaultAsync(s => s.AssignedBhwId == userId);
                    if (sitioDirect != null) return sitioDirect;
                }
                catch { /* continue */ }

                // 2) fallback: mapping table (SitioBhws etc.)
                var ctxType = _db.GetType();
                var candidateNames = new[] { "SitioBhws", "Sitiobhws", "SitioBhw", "Sitiobhw", "SiteBhw", "SitioBhwMappings" };

                IQueryable mappingQ = null;
                Type mapElemType = null;

                foreach (var name in candidateNames)
                {
                    var prop = ctxType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (prop == null) continue;
                    var val = prop.GetValue(_db) as IQueryable;
                    if (val != null)
                    {
                        mappingQ = val;
                        mapElemType = mappingQ.ElementType;
                        break;
                    }
                }

                if (mappingQ == null)
                {
                    foreach (var prop in ctxType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!prop.PropertyType.IsGenericType) continue;
                        var gen = prop.PropertyType.GetGenericArguments().FirstOrDefault();
                        if (gen == null) continue;
                        var n = gen.Name.ToLowerInvariant();
                        if ((n.Contains("sitio") && n.Contains("bhw")) || n.Contains("sitiobhw") || n.Contains("sitio_bhw") || n.Contains("mapping"))
                        {
                            var val = prop.GetValue(_db) as IQueryable;
                            if (val != null)
                            {
                                mappingQ = val;
                                mapElemType = val.ElementType;
                                break;
                            }
                        }
                    }
                }

                if (mappingQ != null && mapElemType != null)
                {
                    var bhwProp = mapElemType.GetProperty("BhwId", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                  ?? mapElemType.GetProperty("BHWId", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                  ?? mapElemType.GetProperty("Bhw", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                  ?? mapElemType.GetProperty("ApplicationUserId", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                  ?? mapElemType.GetProperty("ApplicationUser", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                    var sitioIdProp = mapElemType.GetProperty("SitioId", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                   ?? mapElemType.GetProperty("Sitio", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                   ?? mapElemType.GetProperty("SiteId", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                    if (bhwProp != null)
                    {
                        var prm = System.Linq.Expressions.Expression.Parameter(mapElemType, "m");
                        var member = System.Linq.Expressions.Expression.Property(prm, bhwProp);
                        var constant = System.Linq.Expressions.Expression.Constant(userId, typeof(string));
                        var equal = System.Linq.Expressions.Expression.Equal(member, constant);
                        var lambda = System.Linq.Expressions.Expression.Lambda(equal, prm);

                        var whereCall = System.Linq.Expressions.Expression.Call(
                            typeof(Queryable),
                            "Where",
                            new Type[] { mapElemType },
                            mappingQ.Expression,
                            lambda);

                        var whereQuery = mappingQ.Provider.CreateQuery(whereCall);

                        var firstMethod = typeof(Queryable).GetMethods()
                            .First(m => m.Name == "FirstOrDefault" && m.GetParameters().Length == 1)
                            .MakeGenericMethod(mapElemType);

                        var mappingObj = firstMethod.Invoke(null, new object[] { whereQuery });
                        if (mappingObj != null && sitioIdProp != null)
                        {
                            var sitioIdVal = sitioIdProp.GetValue(mappingObj);
                            if (sitioIdVal != null)
                            {
                                int sitioId = sitioIdVal is int i ? i : Convert.ToInt32(sitioIdVal);
                                var sitio = await _db.Sitios.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sitioId);
                                if (sitio != null) return sitio;
                            }
                            else
                            {
                                var navSitioProp = sitioIdProp.PropertyType != null && sitioIdProp.PropertyType != typeof(int) ? sitioIdProp : null;
                                if (navSitioProp != null)
                                {
                                    var navSitioObj = navSitioProp.GetValue(mappingObj);
                                    if (navSitioObj != null)
                                    {
                                        var idProp = navSitioObj.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                                        if (idProp != null)
                                        {
                                            var idVal = idProp.GetValue(navSitioObj);
                                            if (idVal != null)
                                            {
                                                var sitioId = Convert.ToInt32(idVal);
                                                var sitio = await _db.Sitios.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sitioId);
                                                if (sitio != null) return sitio;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // resilient: ignore exceptions here
            }

            return null;
        }

        private async Task EnsureAssignedSitioInViewBagAsync()
        {
            var sitio = await GetAssignedSitioAsync();
            if (sitio != null)
            {
                ViewBag.AssignedSitioName = sitio.Name ?? "";
                ViewBag.AssignedSitioId = sitio.Id;
            }
            else
            {
                ViewBag.AssignedSitioName = null;
                ViewBag.AssignedSitioId = null;
            }
        }
        #endregion

        #region Audit mapping for BHW dashboard
        private static string MapBhwAuditToText(AuditLog a)
        {
            var action = a.Action ?? "";
            var details = a.Details ?? "";

            if (action.Contains("Create", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.EntityType, "Household", StringComparison.OrdinalIgnoreCase))
                return $"Added household — {details}";

            if (action.Contains("Edit", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.EntityType, "Household", StringComparison.OrdinalIgnoreCase))
                return $"Edited household — {details}";

            if (action.Contains("Archive", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.EntityType, "Household", StringComparison.OrdinalIgnoreCase))
                return $"Archived household — {details}";

            if (action.Contains("Restore", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.EntityType, "Household", StringComparison.OrdinalIgnoreCase))
                return $"Restored household — {details}";

            if (string.Equals(a.EntityType, "Resident", StringComparison.OrdinalIgnoreCase))
            {
                if (action.Contains("Create", StringComparison.OrdinalIgnoreCase))
                    return $"Added resident — {details}";
                if (action.Contains("Edit", StringComparison.OrdinalIgnoreCase))
                    return $"Updated resident — {details}";
                if (action.Contains("Delete", StringComparison.OrdinalIgnoreCase))
                    return $"Removed resident — {details}";
            }

            if (string.Equals(a.EntityType, "Sitio", StringComparison.OrdinalIgnoreCase) &&
                (action.Contains("Assign", StringComparison.OrdinalIgnoreCase) || action.Contains("Update", StringComparison.OrdinalIgnoreCase)))
            {
                return $"Updated sitio — {details}";
            }

            // suppress admin/usermanagement entries
            var lower = action.ToLowerInvariant();
            if (lower.Contains("user") || lower.Contains("role") || lower.Contains("activate") || lower.Contains("deactivate") || lower.Contains("password"))
            {
                return "";
            }

            return $"{action} {details}".Trim();
        }
        #endregion

        private async Task<BhwDashboardVm> BuildDashboardVmAsync()
        {
            // ensure sitio shown in layout
            var sitio = await GetAssignedSitioAsync();
            if (sitio != null)
            {
                ViewBag.AssignedSitioName = sitio.Name ?? "";
                ViewBag.AssignedSitioId = sitio.Id;
            }
            else
            {
                ViewBag.AssignedSitioName = null;
                ViewBag.AssignedSitioId = null;
            }

            // households (non-archived), optionally scoped to sitio
            IQueryable<Household> householdQuery = _db.Households.AsNoTracking().Where(h => !h.IsArchived);
            if (sitio != null) householdQuery = householdQuery.Where(h => h.SitioId == sitio.Id);

            var totalHouseholds = await householdQuery.CountAsync();

            // residents belonging to the filtered households
            var residentQuery = from r in _db.Residents.AsNoTracking()
                                join h in householdQuery on r.HouseholdId equals h.Id
                                select r;

            var totalPopulation = await residentQuery.CountAsync();

            var maleCount = await residentQuery
                .Where(r => r.Sex != null && (r.Sex.ToLower() == "male" || r.Sex.ToLower() == "m"))
                .CountAsync();

            var femaleCount = await residentQuery
                .Where(r => r.Sex != null && (r.Sex.ToLower() == "female" || r.Sex.ToLower() == "f"))
                .CountAsync();

            // fallback to boolean fields if sex strings not present
            if (maleCount + femaleCount == 0 && totalPopulation > 0)
            {
                try
                {
                    var resType = typeof(Resident);
                    var boolProps = new[] { "IsMale", "Male", "IsFemale" };

                    foreach (var pName in boolProps)
                    {
                        var prop = resType.GetProperty(pName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (prop == null) continue;
                        if (prop.PropertyType != typeof(bool) && prop.PropertyType != typeof(bool?)) continue;

                        if (pName.ToLower().Contains("female"))
                        {
                            femaleCount = await residentQuery.Where(r => EF.Property<bool?>(r, prop.Name) == true).CountAsync();
                            maleCount = Math.Max(0, totalPopulation - femaleCount);
                        }
                        else
                        {
                            maleCount = await residentQuery.Where(r => EF.Property<bool?>(r, prop.Name) == true).CountAsync();
                            femaleCount = Math.Max(0, totalPopulation - maleCount);
                        }

                        break;
                    }
                }
                catch { /* ignore */ }
            }

            // recent activity (BHW-focused)
            var recentActivities = new List<DashboardViewModel>();
            try
            {
                var allowedEntities = new[] { "Household", "Resident", "HouseholdHealth", "HouseholdSanitation", "Sitio" };

                var audits = await _db.AuditLogs
                    .AsNoTracking()
                    .Where(a => a.EventTime != null && allowedEntities.Contains(a.EntityType))
                    .OrderByDescending(a => a.EventTime)
                    .Take(30)
                    .ToListAsync();

                foreach (var a in audits)
                {
                    var text = MapBhwAuditToText(a);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    recentActivities.Add(new DashboardViewModel
                    {
                        Timestamp = a.EventTime,
                        Action = a.Action ?? "",
                        Details = text
                    });
                }
            }
            catch
            {
                // don't break dashboard on audit lookup errors
            }

            // ---------------- Build monthly trend (year/month grouping) ----------------
            var trendLabels = new List<string>();
            var trendValues = new List<int>();

            try
            {
                // Build a list of local month starts for the last 6 months (including current)
                var nowLocal = DateTime.Now;
                var monthStarts = Enumerable.Range(-5, 6)
                    .Select(offset => new DateTime(nowLocal.Year, nowLocal.Month, 1).AddMonths(offset))
                    .ToList();

                // detect CreatedAt-like property
                var resType = typeof(Resident);
                var createdProp = resType.GetProperty("CreatedAt", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                  ?? resType.GetProperty("CreatedOn", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                  ?? resType.GetProperty("DateCreated", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                if (createdProp != null && (createdProp.PropertyType == typeof(DateTime) || createdProp.PropertyType == typeof(DateTime?)))
                {
                    // For each calendar month, count residents whose CreatedAt month/year match.
                    foreach (var ms in monthStarts)
                    {
                        int yr = ms.Year;
                        int mon = ms.Month;

                        var cnt = await residentQuery
                            .Where(r => EF.Property<DateTime?>(r, createdProp.Name) != null
                                        && EF.Property<DateTime?>(r, createdProp.Name).Value.Year == yr
                                        && EF.Property<DateTime?>(r, createdProp.Name).Value.Month == mon)
                            .CountAsync();

                        trendLabels.Add(ms.ToString("MMM yyyy"));
                        trendValues.Add(cnt);
                    }
                }
                else
                {
                    // No created timestamp available: avoid pretending history exists.
                    // Fill earlier months with 0 and current month with actual totalPopulation.
                    for (int i = 0; i < monthStarts.Count; i++)
                    {
                        trendLabels.Add(monthStarts[i].ToString("MMM yyyy"));
                        trendValues.Add(i == monthStarts.Count - 1 ? totalPopulation : 0);
                    }
                }
            }
            catch
            {
                // In case of any error, fallback to showing current total only
                var now = DateTime.Now;
                for (int i = 5; i >= 0; i--)
                {
                    var m = now.AddMonths(-i);
                    trendLabels.Add(m.ToString("MMM yyyy"));
                    trendValues.Add(i == 0 ? totalPopulation : 0);
                }
            }

            // Assemble VM
            var vm = new BhwDashboardVm
            {
                UserEmail = User.Identity?.Name ?? "",
                TotalPopulation = totalPopulation,
                TotalHouseholds = totalHouseholds,
                TotalFemale = femaleCount,
                TotalMale = maleCount,
                RecentActivities = recentActivities,
                TrendLabels = trendLabels,
                TrendValues = trendValues
            };

            // Backwards compat
            ViewBag.TotalPopulation = vm.TotalPopulation;
            ViewBag.TotalHouseholds = vm.TotalHouseholds;
            ViewBag.TotalFemale = vm.TotalFemale;
            ViewBag.TotalMale = vm.TotalMale;
            ViewBag.RecentActivities = recentActivities;

            return vm;
        }



        // ---------------- Pages ----------------

        public async Task<IActionResult> Index()
        {
            var vm = await BuildDashboardVmAsync();
            return View(vm);
        }

        // GET: list only non-archived households for this BHW's sitio
        public async Task<IActionResult> Households()
        {
            var sitio = await GetAssignedSitioAsync();
            if (sitio != null)
            {
                ViewBag.AssignedSitioName = sitio.Name;
                ViewBag.AssignedSitioId = sitio.Id;
            }
            else
            {
                ViewBag.AssignedSitioName = null;
                ViewBag.AssignedSitioId = null;
            }

            IQueryable<Household> query = _db.Households
                                             .AsNoTracking()
                                             .Include(h => h.Sitio)
                                             .Where(h => !h.IsArchived);

            if (sitio != null)
            {
                query = query.Where(h => h.SitioId == sitio.Id);
            }

            var list = await query.OrderBy(h => h.Id).ToListAsync();
            return View(list);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ArchiveHousehold(int id, string from = null)
        {
            var hh = await _db.Households.FindAsync(id);
            if (hh == null) return NotFound();

            hh.IsArchived = true;
            hh.ArchivedAt = DateTime.UtcNow;
            hh.ArchivedBy = User?.Identity?.Name ?? "unknown";

            _db.Households.Update(hh);
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] = "Household archived.";

            if (string.Equals(from, "archived", StringComparison.OrdinalIgnoreCase))
                return RedirectToAction(nameof(ArchivedHouseholds));

            return RedirectToAction(nameof(Households));
        }

        [HttpGet]
        public async Task<IActionResult> ArchivedHouseholds()
        {
            var sitio = await GetAssignedSitioAsync();
            if (sitio != null)
            {
                ViewBag.AssignedSitioName = sitio.Name;
                ViewBag.AssignedSitioId = sitio.Id;
            }
            else
            {
                ViewBag.AssignedSitioName = null;
                ViewBag.AssignedSitioId = null;
            }

            IQueryable<Household> query = _db.Households
                                             .AsNoTracking()
                                             .Include(h => h.Sitio)
                                             .Where(h => h.IsArchived);

            if (sitio != null)
            {
                query = query.Where(h => h.SitioId == sitio.Id);
            }

            var list = await query.OrderBy(h => h.Id).ToListAsync();

            return View(list);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RestoreHousehold(int id, string from = null)
        {
            var hh = await _db.Households.FindAsync(id);
            if (hh == null) return NotFound();

            hh.IsArchived = false;
            hh.ArchivedAt = null;
            hh.ArchivedBy = null;

            _db.Households.Update(hh);
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] = "Household restored.";

            if (string.Equals(from, "archived", StringComparison.OrdinalIgnoreCase))
                return RedirectToAction(nameof(ArchivedHouseholds));

            return RedirectToAction(nameof(Households));
        }

        public IActionResult Settings()
        {
            return View();
        }

        // GET: /Bhw/CreateHousehold
        [HttpGet]
        public async Task<IActionResult> CreateHousehold()
        {
            var sitio = await GetAssignedSitioAsync();
            if (sitio != null)
            {
                ViewBag.AssignedSitioName = sitio.Name;
                ViewBag.AssignedSitioId = sitio.Id;
            }
            else
            {
                ViewBag.AssignedSitioName = null;
                ViewBag.AssignedSitioId = null;
            }

            var vm = new CreateHouseholdVm();
            vm.Children.Add(new ChildVm());
            return View(vm);
        }

        // POST: /Bhw/CreateHousehold
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateHousehold(CreateHouseholdVm vm)
        {
            if (!ModelState.IsValid)
            {
                var ms = ModelState
                    .Where(kvp => kvp.Value.Errors.Count > 0)
                    .Select(kvp => new
                    {
                        Key = kvp.Key,
                        Errors = kvp.Value.Errors.Select(e => string.IsNullOrEmpty(e.ErrorMessage) ? (e.Exception?.Message ?? "Exception with no message") : e.ErrorMessage).ToArray()
                    }).ToArray();

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("ModelState is invalid. Errors:");
                foreach (var entry in ms) sb.AppendLine($" - {entry.Key}: {string.Join("; ", entry.Errors)}");

                _logger.LogWarning("CreateHousehold: modelstate invalid: {Errors}", sb.ToString());
                TempData["ErrorMessage"] = sb.ToString();
                return View(vm);
            }

            string familyHead = null;
            if (!string.IsNullOrWhiteSpace(vm.FatherFirstName) || !string.IsNullOrWhiteSpace(vm.FatherLastName))
                familyHead = $"{vm.FatherFirstName} {vm.FatherMiddleName} {vm.FatherLastName} {vm.FatherExtension}".Replace("  ", " ").Trim();
            if (string.IsNullOrWhiteSpace(familyHead))
                familyHead = $"{vm.MotherFirstName} {vm.MotherMiddleName} {vm.MotherLastName} {vm.MotherExtension}".Replace("  ", " ").Trim();
            if (string.IsNullOrWhiteSpace(familyHead)) familyHead = "Unknown";

            var detailsJson = JsonSerializer.Serialize(vm, new JsonSerializerOptions { WriteIndented = false });

            var assignedSitio = await GetAssignedSitioAsync();

            using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                var household = new Household
                {
                    FamilyHead = familyHead,
                    Details = detailsJson,
                    SitioId = assignedSitio?.Id,
                    CreatedAt = DateTime.UtcNow
                };

                _db.Households.Add(household);
                await _db.SaveChangesAsync();

                string UseOther(string selected, string? other) =>
                    string.Equals(selected, "Others", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(other)
                        ? other!.Trim()
                        : (selected ?? "").Trim();

                if (!string.IsNullOrWhiteSpace(vm.FatherFirstName) || !string.IsNullOrWhiteSpace(vm.FatherLastName))
                {
                    var occ = UseOther(vm.FatherOccupation, vm.FatherOccupationOther);
                    var edu = UseOther(vm.FatherEducation, vm.FatherEducationOther);

                    var father = new Resident
                    {
                        HouseholdId = household.Id,
                        Role = "Father",
                        FirstName = vm.FatherFirstName?.Trim() ?? "",
                        MiddleName = vm.FatherMiddleName?.Trim() ?? "",
                        LastName = vm.FatherLastName?.Trim() ?? "",
                        Extension = vm.FatherExtension,
                        DateOfBirth = vm.FatherDateOfBirth,
                        Sex = vm.FatherSex ?? "Male",
                        Occupation = occ ?? "",
                        OccupationOther = string.Equals(occ, vm.FatherOccupationOther, StringComparison.OrdinalIgnoreCase) ? vm.FatherOccupationOther : null,
                        Education = edu ?? "",
                        EducationOther = string.Equals(edu, vm.FatherEducationOther, StringComparison.OrdinalIgnoreCase) ? vm.FatherEducationOther : null,
                        CreatedAt = DateTime.UtcNow
                    };

                    try
                    {
                        var ageProp = typeof(Resident).GetProperty("Age", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        var ageVal = ComputeAge(vm.FatherDateOfBirth);
                        if (ageProp != null && ageVal.HasValue)
                        {
                            if (ageProp.PropertyType == typeof(int) || ageProp.PropertyType == typeof(int?))
                                ageProp.SetValue(father, Convert.ChangeType(ageVal.Value, ageProp.PropertyType));
                        }
                    }
                    catch { /* ignore silently */ }

                    _db.Residents.Add(father);
                }

                if (!string.IsNullOrWhiteSpace(vm.MotherFirstName) || !string.IsNullOrWhiteSpace(vm.MotherLastName))
                {
                    var occ = UseOther(vm.MotherOccupation, vm.MotherOccupationOther);
                    var edu = UseOther(vm.MotherEducation, vm.MotherEducationOther);

                    var mother = new Resident
                    {
                        HouseholdId = household.Id,
                        Role = "Mother",
                        FirstName = vm.MotherFirstName?.Trim() ?? "",
                        MiddleName = vm.MotherMiddleName?.Trim() ?? "",
                        LastName = vm.MotherLastName?.Trim() ?? "",
                        Extension = vm.MotherExtension,
                        DateOfBirth = vm.MotherDateOfBirth,
                        Sex = vm.MotherSex ?? "Female",
                        Occupation = occ ?? "",
                        OccupationOther = string.Equals(occ, vm.MotherOccupationOther, StringComparison.OrdinalIgnoreCase) ? vm.MotherOccupationOther : null,
                        Education = edu ?? "",
                        EducationOther = string.Equals(edu, vm.MotherEducationOther, StringComparison.OrdinalIgnoreCase) ? vm.MotherEducationOther : null,
                        CreatedAt = DateTime.UtcNow
                    };

                    try
                    {
                        var ageProp = typeof(Resident).GetProperty("Age", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        var ageVal = ComputeAge(vm.MotherDateOfBirth);
                        if (ageProp != null && ageVal.HasValue)
                        {
                            if (ageProp.PropertyType == typeof(int) || ageProp.PropertyType == typeof(int?))
                                ageProp.SetValue(mother, Convert.ChangeType(ageVal.Value, ageProp.PropertyType));
                        }
                    }
                    catch { /* ignore silently */ }

                    _db.Residents.Add(mother);
                }

                if (vm.Children != null)
                {
                    foreach (var c in vm.Children)
                    {
                        if (string.IsNullOrWhiteSpace(c.FirstName) && string.IsNullOrWhiteSpace(c.LastName)) continue;

                        var occ = UseOther(c.Occupation, c.OccupationOther);
                        var edu = UseOther(c.Education, c.EducationOther);

                        var child = new Resident
                        {
                            HouseholdId = household.Id,
                            Role = "Child",
                            FirstName = c.FirstName?.Trim() ?? "",
                            MiddleName = c.MiddleName?.Trim() ?? "",
                            LastName = c.LastName?.Trim() ?? "",
                            Extension = c.Extension,
                            DateOfBirth = c.DateOfBirth,
                            Sex = c.Sex ?? "",
                            Occupation = occ ?? "",
                            OccupationOther = string.Equals(occ, c.OccupationOther, StringComparison.OrdinalIgnoreCase) ? c.OccupationOther : null,
                            Education = edu ?? "",
                            EducationOther = string.Equals(edu, c.EducationOther, StringComparison.OrdinalIgnoreCase) ? c.EducationOther : null,
                            CreatedAt = DateTime.UtcNow
                        };
                        try
                        {
                            var ageProp = typeof(Resident).GetProperty("Age", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                            var ageVal = ComputeAge(c.DateOfBirth);
                            if (ageProp != null && ageVal.HasValue)
                            {
                                if (ageProp.PropertyType == typeof(int) || ageProp.PropertyType == typeof(int?))
                                    ageProp.SetValue(child, Convert.ChangeType(ageVal.Value, ageProp.PropertyType));
                            }
                        }
                        catch { /* ignore silently */ }

                        _db.Residents.Add(child);
                    }
                }

                var health = new HouseholdHealth
                {
                    HouseholdId = household.Id,
                    MotherPregnant = vm.MotherPregnant,
                    FamilyPlanning = vm.FamilyPlanning,
                    ExclusiveBreastfeeding = vm.ExclusiveBreastfeeding,
                    MixedFeeding = vm.MixedFeeding,
                    BottleFed = vm.BottleFed,
                    OthersFeeding = vm.OthersFeeding,
                    OthersFeedingSpecify = vm.OthersFeeding ? vm.OthersFeedingSpecify : null,
                    UsingIodizedSalt = vm.UsingIodizedSalt,
                    UsingIFR = vm.UsingIFR
                };
                _db.HouseholdHealth.Add(health);

                var sanitation = new HouseholdSanitation
                {
                    HouseholdId = household.Id,
                    ToiletType = UseOther(vm.ToiletType, vm.ToiletTypeOther),
                    ToiletTypeOther = string.Equals(vm.ToiletType, "Others", StringComparison.OrdinalIgnoreCase) ? vm.ToiletTypeOther : null,
                    FoodProductionActivity = UseOther(vm.FoodProductionActivity, vm.FoodProductionActivityOther),
                    FoodProductionActivityOther = string.Equals(vm.FoodProductionActivity, "Others", StringComparison.OrdinalIgnoreCase) ? vm.FoodProductionActivityOther : null,
                    WaterSource = UseOther(vm.WaterSource, vm.WaterSourceOther),
                    WaterSourceOther = string.Equals(vm.WaterSource, "Others", StringComparison.OrdinalIgnoreCase) ? vm.WaterSourceOther : null
                };
                _db.HouseholdSanitation.Add(sanitation);

                await _db.SaveChangesAsync();
                await tx.CommitAsync();

                TempData["SuccessMessage"] = "Household added successfully.";
                return RedirectToAction(nameof(Households));
            }
            catch (Exception ex)
            {
                string GetFullMessage(Exception e)
                {
                    var sb = new System.Text.StringBuilder();
                    Exception cur = e;
                    while (cur != null)
                    {
                        sb.AppendLine(cur.Message);
                        cur = cur.InnerException;
                    }
                    return sb.ToString();
                }

                try { await tx.RollbackAsync(); } catch { /* ignore */ }

                var full = GetFullMessage(ex);
                _logger.LogError(ex, "CreateHousehold failed: {Message}", full);
                TempData["ErrorMessage"] = "Error saving household: " + full;
                ModelState.AddModelError("", "Validation failed. Check server logs for details.");
                return View(vm);
            }
        }

        // GET: /Bhw/ViewHousehold/5
        public async Task<IActionResult> ViewHousehold(int id)
        {
            var hh = await _db.Households
                .Include(h => h.Residents)
                .Include(h => h.Health)
                .Include(h => h.Sanitation)
                .Include(h => h.Sitio)
                .FirstOrDefaultAsync(h => h.Id == id);

            // after var hh = await _db.Households... FirstOrDefaultAsync...
            if (hh?.Residents != null)
            {
                foreach (var r in hh.Residents)
                {
                    _logger.LogDebug("ViewHousehold Resident loaded: Id={Id}, Name={Name}, DOB={DOB}, Age={Age}",
                        r.Id, $"{r.FirstName} {r.LastName}",
                        (r.GetType().GetProperty("DateOfBirth")?.GetValue(r) ?? "<no prop>"),
                        (r.GetType().GetProperty("Age")?.GetValue(r) ?? "<no prop>"));
                }
            }

            if (hh == null) return NotFound();

            await EnsureAssignedSitioInViewBagAsync();

            return View(hh);
        }

        // GET: /Bhw/EditHousehold/5
        [HttpGet]
        public async Task<IActionResult> EditHousehold(int id)
        {
            var hh = await _db.Households
                .Include(h => h.Residents)
                .Include(h => h.Health)
                .Include(h => h.Sanitation)
                .FirstOrDefaultAsync(h => h.Id == id);



            if (hh == null) return NotFound();

            var assigned = await GetAssignedSitioAsync();
            if (assigned != null)
            {
                ViewBag.AssignedSitioName = assigned.Name;
                ViewBag.AssignedSitioId = assigned.Id;
            }
            else
            {
                ViewBag.AssignedSitioName = null;
                ViewBag.AssignedSitioId = null;
            }

            var vm = new CreateHouseholdVm();

            var father = hh.Residents.FirstOrDefault(r => r.Role == "Father");
            var mother = hh.Residents.FirstOrDefault(r => r.Role == "Mother");
            var children = hh.Residents.Where(r => r.Role == "Child").ToList();

            if (father != null)
            {
                vm.FatherFirstName = father.FirstName;
                vm.FatherMiddleName = father.MiddleName;
                vm.FatherLastName = father.LastName;
                vm.FatherExtension = father.Extension;
                vm.FatherOccupation = father.Occupation;
                vm.FatherOccupationOther = father.OccupationOther;
                vm.FatherEducation = father.Education;
                vm.FatherEducationOther = father.EducationOther;
                vm.FatherSex = father.Sex;

                // copy DOB
                vm.FatherDateOfBirth = father.DateOfBirth;
            }

            if (mother != null)
            {
                vm.MotherFirstName = mother.FirstName;
                vm.MotherMiddleName = mother.MiddleName;
                vm.MotherLastName = mother.LastName;
                vm.MotherExtension = mother.Extension;
                vm.MotherOccupation = mother.Occupation;
                vm.MotherOccupationOther = mother.OccupationOther;
                vm.MotherEducation = mother.Education;
                vm.MotherEducationOther = mother.EducationOther;
                vm.MotherSex = mother.Sex;

                // copy DOB
                vm.MotherDateOfBirth = mother.DateOfBirth;
            }

            foreach (var c in children)
            {
                vm.Children.Add(new ChildVm
                {
                    FirstName = c.FirstName,
                    MiddleName = c.MiddleName,
                    LastName = c.LastName,
                    Extension = c.Extension,
                    Occupation = c.Occupation,
                    OccupationOther = c.OccupationOther,
                    Education = c.Education,
                    EducationOther = c.EducationOther,
                    Sex = c.Sex,
                    // copy DOB into the child VM so the edit form shows it
                    DateOfBirth = c.DateOfBirth
                });
            }


            if (hh.Health != null)
            {
                vm.MotherPregnant = hh.Health.MotherPregnant;
                vm.FamilyPlanning = hh.Health.FamilyPlanning;
                vm.ExclusiveBreastfeeding = hh.Health.ExclusiveBreastfeeding;
                vm.MixedFeeding = hh.Health.MixedFeeding;
                vm.BottleFed = hh.Health.BottleFed;
                vm.OthersFeeding = hh.Health.OthersFeeding;
                vm.OthersFeedingSpecify = hh.Health.OthersFeedingSpecify;
                vm.UsingIodizedSalt = hh.Health.UsingIodizedSalt;
                vm.UsingIFR = hh.Health.UsingIFR;
            }

            if (hh.Sanitation != null)
            {
                vm.ToiletType = hh.Sanitation.ToiletType;
                vm.ToiletTypeOther = hh.Sanitation.ToiletTypeOther;
                vm.FoodProductionActivity = hh.Sanitation.FoodProductionActivity;
                vm.FoodProductionActivityOther = hh.Sanitation.FoodProductionActivityOther;
                vm.WaterSource = hh.Sanitation.WaterSource;
                vm.WaterSourceOther = hh.Sanitation.WaterSourceOther;
            }

            TempData["EditingHouseholdId"] = hh.Id;

            return View(vm);
        }

        // POST: /Bhw/EditHousehold
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditHousehold(CreateHouseholdVm vm)
        {
            if (!ModelState.IsValid)
            {
                TempData["ErrorMessage"] = "Validation failed. Check the inputs.";
                return View(vm);
            }

            if (!TempData.ContainsKey("EditingHouseholdId"))
            {
                ModelState.AddModelError("", "Editing session expired. Please open the form again.");
                return View(vm);
            }

            var idObj = TempData["EditingHouseholdId"];
            if (!int.TryParse(idObj?.ToString(), out var householdId))
            {
                ModelState.AddModelError("", "Invalid household id.");
                return View(vm);
            }

            var assigned = await GetAssignedSitioAsync();
            if (assigned != null)
            {
                ViewBag.AssignedSitioName = assigned.Name;
                ViewBag.AssignedSitioId = assigned.Id;
            }
            else
            {
                ViewBag.AssignedSitioName = null;
                ViewBag.AssignedSitioId = null;
            }

            using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                var household = await _db.Households
                    .Include(h => h.Residents)
                    .Include(h => h.Health)
                    .Include(h => h.Sanitation)
                    .FirstOrDefaultAsync(h => h.Id == householdId);

                if (household == null) return NotFound();

                // local helper (same as CreateHousehold) to handle "Others" selections
                string UseOther(string selected, string? other) =>
                    string.Equals(selected, "Others", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(other)
                        ? other!.Trim()
                        : (selected ?? "").Trim();

                // compute family head (same logic as CreateHousehold)
                string familyHead = null;
                if (!string.IsNullOrWhiteSpace(vm.FatherFirstName) || !string.IsNullOrWhiteSpace(vm.FatherLastName))
                    familyHead = $"{vm.FatherFirstName} {vm.FatherMiddleName} {vm.FatherLastName} {vm.FatherExtension}".Replace("  ", " ").Trim();

                if (string.IsNullOrWhiteSpace(familyHead))
                    familyHead = $"{vm.MotherFirstName} {vm.MotherMiddleName} {vm.MotherLastName} {vm.MotherExtension}".Replace("  ", " ").Trim();

                if (string.IsNullOrWhiteSpace(familyHead))
                    familyHead = "Unknown";

                household.FamilyHead = familyHead;
                household.Details = JsonSerializer.Serialize(vm, new JsonSerializerOptions { WriteIndented = false });
                household.UpdatedAt = DateTime.UtcNow;

                // remove old residents and re-add from VM
                var toRemove = household.Residents.ToList();
                if (toRemove.Any())
                {
                    _db.Residents.RemoveRange(toRemove);
                    await _db.SaveChangesAsync();
                }

                // helper to set Age: prefer direct property assignment if mapped, else fallback to reflection
                void SetResidentAgeIfPossible(Resident r, DateTime? dob)
                {
                    var ageVal = ComputeAge(dob); // int?
                    if (ageVal.HasValue)
                    {
                        // try direct property (most common, mapped case)
                        var agePropInfo = typeof(Resident).GetProperty("Age", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (agePropInfo != null && (agePropInfo.PropertyType == typeof(int) || agePropInfo.PropertyType == typeof(int?)))
                        {
                            agePropInfo.SetValue(r, ageVal.Value);
                            return;
                        }

                        // as a last resort, try dynamic/expando reflection on runtime type (if model differs)
                        try
                        {
                            var runtimeProp = r.GetType().GetProperty("Age", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                            if (runtimeProp != null && (runtimeProp.PropertyType == typeof(int) || runtimeProp.PropertyType == typeof(int?)))
                            {
                                runtimeProp.SetValue(r, ageVal.Value);
                            }
                        }
                        catch { /* ignore */ }
                    }
                }

                // Father
                if (!string.IsNullOrWhiteSpace(vm.FatherFirstName) || !string.IsNullOrWhiteSpace(vm.FatherLastName))
                {
                    var occ = UseOther(vm.FatherOccupation, vm.FatherOccupationOther);
                    var edu = UseOther(vm.FatherEducation, vm.FatherEducationOther);

                    var fatherRes = new Resident
                    {
                        HouseholdId = household.Id,
                        Role = "Father",
                        FirstName = vm.FatherFirstName?.Trim() ?? "",
                        MiddleName = vm.FatherMiddleName?.Trim() ?? "",
                        LastName = vm.FatherLastName?.Trim() ?? "",
                        Extension = vm.FatherExtension,
                        DateOfBirth = vm.FatherDateOfBirth,
                        Sex = vm.FatherSex ?? "Male",
                        Occupation = occ ?? "",
                        OccupationOther = string.Equals(occ, vm.FatherOccupationOther, StringComparison.OrdinalIgnoreCase) ? vm.FatherOccupationOther : null,
                        Education = edu ?? "",
                        EducationOther = string.Equals(edu, vm.FatherEducationOther, StringComparison.OrdinalIgnoreCase) ? vm.FatherEducationOther : null
                    };

                    // set Age explicitly so EF can persist it
                    SetResidentAgeIfPossible(fatherRes, fatherRes.DateOfBirth);

                    _db.Residents.Add(fatherRes);
                }

                // Mother
                if (!string.IsNullOrWhiteSpace(vm.MotherFirstName) || !string.IsNullOrWhiteSpace(vm.MotherLastName))
                {
                    var occ = UseOther(vm.MotherOccupation, vm.MotherOccupationOther);
                    var edu = UseOther(vm.MotherEducation, vm.MotherEducationOther);

                    var motherRes = new Resident
                    {
                        HouseholdId = household.Id,
                        Role = "Mother",
                        FirstName = vm.MotherFirstName?.Trim() ?? "",
                        MiddleName = vm.MotherMiddleName?.Trim() ?? "",
                        LastName = vm.MotherLastName?.Trim() ?? "",
                        Extension = vm.MotherExtension,
                        DateOfBirth = vm.MotherDateOfBirth,
                        Sex = vm.MotherSex ?? "Female",
                        Occupation = occ ?? "",
                        OccupationOther = string.Equals(occ, vm.MotherOccupationOther, StringComparison.OrdinalIgnoreCase) ? vm.MotherOccupationOther : null,
                        Education = edu ?? "",
                        EducationOther = string.Equals(edu, vm.MotherEducationOther, StringComparison.OrdinalIgnoreCase) ? vm.MotherEducationOther : null
                    };

                    SetResidentAgeIfPossible(motherRes, motherRes.DateOfBirth);
                    _db.Residents.Add(motherRes);
                }

                // Children
                if (vm.Children != null)
                {
                    foreach (var c in vm.Children)
                    {
                        if (string.IsNullOrWhiteSpace(c.FirstName) && string.IsNullOrWhiteSpace(c.LastName)) continue;

                        var occ = UseOther(c.Occupation, c.OccupationOther);
                        var edu = UseOther(c.Education, c.EducationOther);

                        var childRes = new Resident
                        {
                            HouseholdId = household.Id,
                            Role = "Child",
                            FirstName = c.FirstName?.Trim() ?? "",
                            MiddleName = c.MiddleName?.Trim() ?? "",
                            LastName = c.LastName?.Trim() ?? "",
                            Extension = c.Extension,
                            DateOfBirth = c.DateOfBirth,
                            Sex = c.Sex ?? "",
                            Occupation = occ ?? "",
                            OccupationOther = string.Equals(occ, c.OccupationOther, StringComparison.OrdinalIgnoreCase) ? c.OccupationOther : null,
                            Education = edu ?? "",
                            EducationOther = string.Equals(edu, c.EducationOther, StringComparison.OrdinalIgnoreCase) ? c.EducationOther : null
                        };

                        SetResidentAgeIfPossible(childRes, childRes.DateOfBirth);
                        _db.Residents.Add(childRes);
                    }
                }

                // Health
                if (household.Health == null)
                    household.Health = new HouseholdHealth { HouseholdId = household.Id };

                household.Health.MotherPregnant = vm.MotherPregnant;
                household.Health.FamilyPlanning = vm.FamilyPlanning;
                household.Health.ExclusiveBreastfeeding = vm.ExclusiveBreastfeeding;
                household.Health.MixedFeeding = vm.MixedFeeding;
                household.Health.BottleFed = vm.BottleFed;
                household.Health.OthersFeeding = vm.OthersFeeding;
                household.Health.OthersFeedingSpecify = vm.OthersFeeding ? vm.OthersFeedingSpecify : null;
                household.Health.UsingIodizedSalt = vm.UsingIodizedSalt;
                household.Health.UsingIFR = vm.UsingIFR;

                // Sanitation
                if (household.Sanitation == null)
                    household.Sanitation = new HouseholdSanitation { HouseholdId = household.Id };

                household.Sanitation.ToiletType = UseOther(vm.ToiletType, vm.ToiletTypeOther);
                household.Sanitation.ToiletTypeOther = string.Equals(vm.ToiletType, "Others", StringComparison.OrdinalIgnoreCase) ? vm.ToiletTypeOther : null;
                household.Sanitation.FoodProductionActivity = UseOther(vm.FoodProductionActivity, vm.FoodProductionActivityOther);
                household.Sanitation.FoodProductionActivityOther = string.Equals(vm.FoodProductionActivity, "Others", StringComparison.OrdinalIgnoreCase) ? vm.FoodProductionActivityOther : null;
                household.Sanitation.WaterSource = UseOther(vm.WaterSource, vm.WaterSourceOther);
                household.Sanitation.WaterSourceOther = string.Equals(vm.WaterSource, "Others", StringComparison.OrdinalIgnoreCase) ? vm.WaterSourceOther : null;

                await _db.SaveChangesAsync();
                await tx.CommitAsync();

                TempData["SuccessMessage"] = "Household updated successfully.";
                return RedirectToAction(nameof(Households));
            }
            catch (Exception ex)
            {
                try { await tx.RollbackAsync(); } catch { }
                _logger.LogError(ex, "EditHousehold failed");
                ModelState.AddModelError("", "Error updating household: " + ex.Message);
                return View(vm);
            }
        }


        // GET: /Bhw/Reports
        [HttpGet]
        public async Task<IActionResult> Reports()
        {
            return View();
        }
    }
}
