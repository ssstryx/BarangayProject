using BarangayProject.Data;
using BarangayProject.Models;
using BarangayProject.Models.AdminModel;
using BarangayProject.Models.BhwModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

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

                // 2) fallback: mapping table (SitioBhws, SitioBhwMappings etc.)
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
                                var navProp = sitioIdProp.PropertyType;
                                if (navProp != null && sitioIdProp.PropertyType != typeof(int))
                                {
                                    var navSitioObj = sitioIdProp.GetValue(mappingObj);
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
                // ignore
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

        #region Audit mapping for BHW dashboard
        private static string MapBhwAuditToText(AuditLog a)
        {
            var action = a?.Action ?? "";
            var details = a?.Details ?? "";

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

            var lower = action.ToLowerInvariant();
            if (lower.Contains("user") || lower.Contains("role") || lower.Contains("activate") || lower.Contains("deactivate") || lower.Contains("password"))
            {
                return "";
            }

            return $"{action} {details}".Trim();
        }
        #endregion

        // Helper that returns a safe DateTime for ordering audit rows
        private static DateTime GetSafeTimestampFromAudit(object auditObj)
        {
            if (auditObj == null) return DateTime.UtcNow;

            try
            {
                var t = auditObj.GetType();

                // prefer EventTime if present
                var evProp = t.GetProperty("EventTime", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (evProp != null)
                {
                    var evVal = evProp.GetValue(auditObj);
                    // when boxed, both DateTime and non-null DateTime? become a boxed DateTime
                    if (evVal is DateTime dt && dt != default(DateTime)) return dt;
                }

                // try CreatedAt / CreatedOn / DateCreated
                var createdProp = t.GetProperty("CreatedAt", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                  ?? t.GetProperty("CreatedOn", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                  ?? t.GetProperty("DateCreated", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                if (createdProp != null)
                {
                    var cVal = createdProp.GetValue(auditObj);
                    if (cVal is DateTime cdt && cdt != default(DateTime)) return cdt;
                }
            }
            catch
            {
                // ignore reflection errors
            }

            return DateTime.UtcNow;
        }

        // original relevance heuristic (kept as helper)
        private static bool IsRelevantAudit(AuditLog a)
        {
            if (a == null) return false;
            var et = (a.EntityType ?? "").ToLowerInvariant();
            var action = (a.Action ?? "").ToLowerInvariant();
            var details = (a.Details ?? "").ToLowerInvariant();

            // Exclude admin/user management entries explicitly
            if (action.Contains("user") || action.Contains("role") || action.Contains("password") ||
                action.Contains("activate") || action.Contains("deactivate") || details.Contains("user") || details.Contains("role"))
                return false;

            // Allowed entity types / keywords for BHW dashboard (case-insensitive)
            var allowedEntities = new[] { "household", "resident", "sitio", "sitiobhw", "householdhealth", "householdsanitation" };
            var allowedActions = new[] { "create", "edit", "update", "archive", "restore", "assign", "delete" };

            // If entity type is known and allowed -> include
            if (!string.IsNullOrWhiteSpace(et))
            {
                foreach (var e in allowedEntities)
                    if (et.Contains(e)) return true;
            }

            // Otherwise include when action contains relevant keywords AND entity/details mention household/resident/sitio
            foreach (var kw in allowedActions)
            {
                if (action.Contains(kw) || details.Contains(kw))
                {
                    if (details.Contains("household") || details.Contains("resident") || details.Contains("sitio"))
                        return true;
                }
            }

            return false;
        }

        private async Task<BhwDashboardVm> BuildDashboardVmAsync()
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

            IQueryable<Household> householdQuery = _db.Households.AsNoTracking().Where(h => (h.IsArchived == null || h.IsArchived == false));
            if (sitio != null) householdQuery = householdQuery.Where(h => h.SitioId == sitio.Id);

            var totalHouseholds = await householdQuery.CountAsync();

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
                // Materialize audit rows to avoid EF translation issues
                var auditRows = await _db.AuditLogs.AsNoTracking().ToListAsync();

                var userId = _userManager.GetUserId(User);
                int? assignedSitioId = sitio?.Id;
                var assignedSitioNameLower = sitio?.Name?.ToLowerInvariant();

                // Prepare admin-exclusion lists (targeted)
                var adminEntityNames = new[] { "sitio", "sitiobhw", "bhw", "user", "role" };
                var adminActionMarkers = new[] { "createsitio", "deletesitio", "assignbhw", "updatesitio", "createsitiobhw", "deletesitiobhw" };

                var top = auditRows.Where(a =>
                {
                    if (a == null) return false;

                    var actionLower = (a.Action ?? "").ToLowerInvariant();
                    var entityLower = (a.EntityType ?? "").ToLowerInvariant();
                    var detailsLower = (a.Details ?? "").ToLowerInvariant();

                    // 1) HARD exclude admin/site management events (targeted)
                    // Exclude rows that are explicitly about Sitio management or user/role administration
                    if (!string.IsNullOrWhiteSpace(entityLower) && adminEntityNames.Any(e => entityLower.Contains(e)))
                    {
                        // If the entity is "household" or "resident" we keep; only drop Sitio/user/role admin entries
                        if (entityLower.Contains("sitio") || entityLower.Contains("user") || entityLower.Contains("role") || entityLower.Contains("bhw"))
                            return false;
                    }

                    if (!string.IsNullOrWhiteSpace(actionLower) && adminActionMarkers.Any(m => actionLower.Contains(m)))
                        return false;

                    // 2) include if audit was performed by this BHW (UserId match)
                    if (!string.IsNullOrWhiteSpace(a.UserId) && !string.IsNullOrWhiteSpace(userId) && a.UserId == userId)
                        return true;

                    // 3) include if audit references assigned sitio id via EntityId (when EntityType refers to Sitio or mapping)
                    if (!string.IsNullOrWhiteSpace(a.EntityId) && assignedSitioId.HasValue)
                    {
                        if (int.TryParse(a.EntityId, out var parsedId) && parsedId == assignedSitioId.Value)
                            return true;
                    }

                    // 4) include when EntityType explicitly is a household/resident/health/sanitation entity
                    if (!string.IsNullOrWhiteSpace(entityLower))
                    {
                        if (entityLower.Contains("household") || entityLower.Contains("resident") || entityLower.Contains("householdhealth") || entityLower.Contains("householdsanitation"))
                            return true;
                    }

                    // 5) include if details mention the assigned sitio name (fallback)
                    if (!string.IsNullOrWhiteSpace(assignedSitioNameLower) && !string.IsNullOrWhiteSpace(detailsLower))
                    {
                        if (detailsLower.Contains(assignedSitioNameLower))
                            return true;
                    }

                    // 6) final fallback: use original heuristic (action/details mention household/resident/sitio keywords)
                    return IsRelevantAudit(a);
                })
                .OrderByDescending(a => a.EventTime != default ? a.EventTime : a.CreatedAt)
                .Take(30)
                .ToList();

                foreach (var a in top)
                {
                    try
                    {
                        var ts = a.EventTime != default(DateTime) ? a.EventTime : a.CreatedAt;

                        // map to friendly text; MapBhwAuditToText already returns "" for admin/usermanagement entries
                        var mapped = MapBhwAuditToText(a);
                        var rawDetails = a.Details ?? "";
                        var fallback = $"{(a.Action ?? "")} {rawDetails}".Trim();
                        var detailsToShow = !string.IsNullOrWhiteSpace(mapped) ? mapped : fallback;

                        // if mapping returns empty, skip (safeguard)
                        if (string.IsNullOrWhiteSpace(detailsToShow)) continue;

                        recentActivities.Add(new DashboardViewModel
                        {
                            Timestamp = ts,
                            Action = a.Action ?? "",
                            Details = detailsToShow
                        });
                    }
                    catch (Exception exRow)
                    {
                        _logger.LogWarning(exRow, "BuildDashboardVmAsync: failed processing an audit row for display.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BuildDashboardVmAsync: failed while loading recent activities.");
                recentActivities = recentActivities ?? new List<DashboardViewModel>();
            }


            var trendLabels = new List<string>();
            var trendValues = new List<int>();

            try
            {
                var nowLocal = DateTime.Now;
                var monthStarts = Enumerable.Range(-5, 6)
                    .Select(offset => new DateTime(nowLocal.Year, nowLocal.Month, 1).AddMonths(offset))
                    .ToList();

                var resType = typeof(Resident);
                var createdProp = resType.GetProperty("CreatedAt", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                  ?? resType.GetProperty("CreatedOn", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                  ?? resType.GetProperty("DateCreated", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                if (createdProp != null && (createdProp.PropertyType == typeof(DateTime) || createdProp.PropertyType == typeof(DateTime?)))
                {
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
                    for (int i = 0; i < monthStarts.Count; i++)
                    {
                        trendLabels.Add(monthStarts[i].ToString("MMM yyyy"));
                        trendValues.Add(i == monthStarts.Count - 1 ? totalPopulation : 0);
                    }
                }
            }
            catch
            {
                var now = DateTime.Now;
                for (int i = 5; i >= 0; i--)
                {
                    var m = now.AddMonths(-i);
                    trendLabels.Add(m.ToString("MMM yyyy"));
                    trendValues.Add(i == 0 ? totalPopulation : 0);
                }
            }

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
                                             .Where(h => (h.IsArchived == null || h.IsArchived == false));

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
                                             .Where(h => h.IsArchived == true);

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

            if (hh?.Residents != null)
            {
                hh.Residents = hh.Residents.Where(r =>
                    (r.GetType().GetProperty("IsArchived") == null) // no property -> keep
                    || !(bool)(r.GetType().GetProperty("IsArchived").GetValue(r) ?? false)
                ).ToList();
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
            await EnsureAssignedSitioInViewBagAsync();

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

            if (!int.TryParse(TempData["EditingHouseholdId"]?.ToString(), out var householdId))
            {
                ModelState.AddModelError("", "Invalid household id.");
                return View(vm);
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

                string UseOther(string selected, string? other) =>
                    string.Equals(selected, "Others", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(other)
                        ? other!.Trim()
                        : (selected ?? "").Trim();

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

                var existingResidents = household.Residents?.ToList() ?? new List<Resident>();
                var matchedResidentIds = new HashSet<int>();

                Resident? FindExistingByRole(string role)
                {
                    return existingResidents.FirstOrDefault(r => string.Equals(r.Role ?? "", role, StringComparison.OrdinalIgnoreCase));
                }

                // Father
                if (!string.IsNullOrWhiteSpace(vm.FatherFirstName) || !string.IsNullOrWhiteSpace(vm.FatherLastName))
                {
                    var father = FindExistingByRole("Father");
                    if (father == null)
                    {
                        father = new Resident { HouseholdId = household.Id, Role = "Father", CreatedAt = DateTime.UtcNow };
                        _db.Residents.Add(father);
                    }

                    father.FirstName = vm.FatherFirstName?.Trim() ?? "";
                    father.MiddleName = vm.FatherMiddleName?.Trim();
                    father.LastName = vm.FatherLastName?.Trim() ?? "";
                    father.Extension = vm.FatherExtension;
                    father.DateOfBirth = vm.FatherDateOfBirth;
                    father.Sex = vm.FatherSex ?? "Male";
                    father.Occupation = UseOther(vm.FatherOccupation, vm.FatherOccupationOther);
                    father.OccupationOther = string.Equals(father.Occupation, vm.FatherOccupationOther, StringComparison.OrdinalIgnoreCase) ? vm.FatherOccupationOther : null;
                    father.Education = UseOther(vm.FatherEducation, vm.FatherEducationOther);
                    father.EducationOther = string.Equals(father.Education, vm.FatherEducationOther, StringComparison.OrdinalIgnoreCase) ? vm.FatherEducationOther : null;

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
                    catch { /* ignore */ }

                    if (father.Id != 0) matchedResidentIds.Add(father.Id);
                }

                // Mother
                if (!string.IsNullOrWhiteSpace(vm.MotherFirstName) || !string.IsNullOrWhiteSpace(vm.MotherLastName))
                {
                    var mother = FindExistingByRole("Mother");
                    if (mother == null)
                    {
                        mother = new Resident { HouseholdId = household.Id, Role = "Mother", CreatedAt = DateTime.UtcNow };
                        _db.Residents.Add(mother);
                    }

                    mother.FirstName = vm.MotherFirstName?.Trim() ?? "";
                    mother.MiddleName = vm.MotherMiddleName?.Trim();
                    mother.LastName = vm.MotherLastName?.Trim() ?? "";
                    mother.Extension = vm.MotherExtension;
                    mother.DateOfBirth = vm.MotherDateOfBirth;
                    mother.Sex = vm.MotherSex ?? "Female";
                    mother.Occupation = UseOther(vm.MotherOccupation, vm.MotherOccupationOther);
                    mother.OccupationOther = string.Equals(mother.Occupation, vm.MotherOccupationOther, StringComparison.OrdinalIgnoreCase) ? vm.MotherOccupationOther : null;
                    mother.Education = UseOther(vm.MotherEducation, vm.MotherEducationOther);
                    mother.EducationOther = string.Equals(mother.Education, vm.MotherEducationOther, StringComparison.OrdinalIgnoreCase) ? vm.MotherEducationOther : null;

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
                    catch { /* ignore */ }

                    if (mother.Id != 0) matchedResidentIds.Add(mother.Id);
                }

                // Children
                var postedChildren = vm.Children ?? new List<ChildVm>();
                var updatedOrAddedResidentEntities = new List<Resident>();

                foreach (var c in postedChildren)
                {
                    int? postedId = null;
                    try
                    {
                        var idProp = c?.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (idProp != null)
                        {
                            var val = idProp.GetValue(c);
                            if (val != null) postedId = Convert.ToInt32(val);
                        }
                    }
                    catch { /* ignore */ }

                    Resident? match = null;
                    if (postedId.HasValue)
                    {
                        match = existingResidents.FirstOrDefault(r => r.Id == postedId.Value);
                    }

                    if (match == null && (!string.IsNullOrWhiteSpace(c.FirstName) || !string.IsNullOrWhiteSpace(c.LastName)))
                    {
                        match = existingResidents.FirstOrDefault(r =>
                            string.Equals(r.Role ?? "", "Child", StringComparison.OrdinalIgnoreCase)
                            && string.Equals((r.FirstName ?? "").Trim(), (c.FirstName ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                            && string.Equals((r.LastName ?? "").Trim(), (c.LastName ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                            && ((r.DateOfBirth == c.DateOfBirth) || (r.DateOfBirth.HasValue && c.DateOfBirth.HasValue && r.DateOfBirth.Value.Date == c.DateOfBirth.Value.Date))
                        );
                    }

                    if (match == null)
                    {
                        match = new Resident
                        {
                            HouseholdId = household.Id,
                            Role = "Child",
                            CreatedAt = DateTime.UtcNow
                        };
                        _db.Residents.Add(match);
                    }

                    match.FirstName = c.FirstName?.Trim() ?? "";
                    match.MiddleName = c.MiddleName?.Trim();
                    match.LastName = c.LastName?.Trim() ?? "";
                    match.Extension = c.Extension;
                    match.DateOfBirth = c.DateOfBirth;
                    match.Sex = c.Sex;
                    match.Occupation = UseOther(c.Occupation, c.OccupationOther);
                    match.OccupationOther = string.Equals(match.Occupation, c.OccupationOther, StringComparison.OrdinalIgnoreCase) ? c.OccupationOther : null;
                    match.Education = UseOther(c.Education, c.EducationOther);
                    match.EducationOther = string.Equals(match.Education, c.EducationOther, StringComparison.OrdinalIgnoreCase) ? c.EducationOther : null;

                    try
                    {
                        var ageProp = typeof(Resident).GetProperty("Age", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        var ageVal = ComputeAge(c.DateOfBirth);
                        if (ageProp != null && ageVal.HasValue)
                        {
                            if (ageProp.PropertyType == typeof(int) || ageProp.PropertyType == typeof(int?))
                                ageProp.SetValue(match, Convert.ChangeType(ageVal.Value, ageProp.PropertyType));
                        }
                    }
                    catch { /* ignore */ }

                    if (match.Id != 0) matchedResidentIds.Add(match.Id);
                    updatedOrAddedResidentEntities.Add(match);
                }

                // Remove children not present anymore
                var existingChildren = existingResidents.Where(r => string.Equals(r.Role ?? "", "Child", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var exChild in existingChildren)
                {
                    if (!matchedResidentIds.Contains(exChild.Id))
                    {
                        _db.Residents.Remove(exChild);
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

                _db.Households.Update(household);
                await _db.SaveChangesAsync();
                await tx.CommitAsync();

                TempData["SuccessMessage"] = "Household updated successfully.";
                return RedirectToAction(nameof(Households));
            }
            catch (Exception ex)
            {
                try { await tx.RollbackAsync(); } catch { /* ignore */ }
                _logger.LogError(ex, "EditHousehold failed");
                ModelState.AddModelError("", "Error updating household: " + ex.Message);
                return View(vm);
            }
        }

        // GET: /Bhw/Reports
        [HttpGet]
        public async Task<IActionResult> Reports()
        {
            await EnsureAssignedSitioInViewBagAsync();
            return View();
        }

        // PRIVATE helper: returns columns and rows (rows are Dictionary<string,string>)
        private async Task<(List<string> columns, List<Dictionary<string, string>> rows)> BuildReportData(
            string reportType, DateTime? startDate, DateTime? endDate)
        {
            var sitio = await GetAssignedSitioAsync();
            DateTime? start = startDate?.Date;
            DateTime? end = endDate.HasValue ? endDate.Value.Date.AddDays(1).AddTicks(-1) : (DateTime?)null;

            var columns = new List<string>();
            var rows = new List<Dictionary<string, string>>();

            var rt = (reportType ?? "households").Replace('_', '-').Trim().ToLowerInvariant();

            if (rt == "households" || rt == "archived-households")
            {
                bool archived = rt.StartsWith("archived");
                IQueryable<Household> q = _db.Households.AsNoTracking().Include(h => h.Sitio);

                if (sitio != null) q = q.Where(h => h.SitioId == sitio.Id);

                var list = await q.OrderBy(h => h.Id).ToListAsync();

                columns = new List<string> { "Id", "FamilyHead", "Sitio", "CreatedAt", "UpdatedAt" };

                foreach (var h in list)
                {
                    bool isArchived = false;
                    var hhType = h.GetType();
                    var isArchivedProp = hhType.GetProperty("IsArchived", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                    var archivedAtProp = hhType.GetProperty("ArchivedAt", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

                    if (isArchivedProp != null)
                    {
                        var v = isArchivedProp.GetValue(h) as bool?;
                        isArchived = v == true;
                    }
                    else if (archivedAtProp != null)
                    {
                        isArchived = archivedAtProp.GetValue(h) != null;
                    }

                    if (archived != isArchived) continue;

                    var createdProp = hhType.GetProperty("CreatedAt", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                    if (createdProp != null && (start.HasValue || end.HasValue))
                    {
                        var createdVal = createdProp.GetValue(h) as DateTime?;
                        if (start.HasValue && (createdVal == null || createdVal < start.Value)) continue;
                        if (end.HasValue && (createdVal == null || createdVal > end.Value)) continue;
                    }

                    rows.Add(new Dictionary<string, string>
                    {
                        ["Id"] = h.Id.ToString(),
                        ["FamilyHead"] = h.FamilyHead ?? "",
                        ["Sitio"] = h.Sitio?.Name ?? "",
                        ["CreatedAt"] = (createdProp != null ? (createdProp.GetValue(h) as DateTime?)?.ToString("yyyy-MM-dd") : (h.GetType().GetProperty("CreatedAt")?.GetValue(h) as DateTime?)?.ToString("yyyy-MM-dd")) ?? "",
                        ["UpdatedAt"] = (h.GetType().GetProperty("UpdatedAt")?.GetValue(h) as DateTime?)?.ToString("yyyy-MM-dd") ?? ""
                    });
                }
            }
            else if (rt == "residents" || rt == "archived-residents")
            {
                bool archived = rt.StartsWith("archived");
                IQueryable<Resident> q = _db.Residents.AsNoTracking().Include(r => r.Household).ThenInclude(h => h.Sitio);

                if (sitio != null) q = q.Where(r => r.Household != null && r.Household.SitioId == sitio.Id);

                var list = await q.OrderBy(r => r.Id).ToListAsync();

                columns = new List<string> { "Id", "FullName", "Role", "DateOfBirth", "Sex", "Occupation", "Education", "HouseholdId", "Sitio" };

                var rType = typeof(Resident);
                var isArchivedProp = rType.GetProperty("IsArchived", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                var archivedAtProp = rType.GetProperty("ArchivedAt", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                var createdProp = rType.GetProperty("CreatedAt", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

                foreach (var r in list)
                {
                    bool isArchived = false;
                    if (isArchivedProp != null)
                    {
                        var v = isArchivedProp.GetValue(r) as bool?;
                        isArchived = v == true;
                    }
                    else if (archivedAtProp != null)
                    {
                        isArchived = archivedAtProp.GetValue(r) != null;
                    }

                    if (archived != isArchived) continue;

                    if (createdProp != null && (start.HasValue || end.HasValue))
                    {
                        var createdVal = createdProp.GetValue(r) as DateTime?;
                        if (start.HasValue && (createdVal == null || createdVal < start.Value)) continue;
                        if (end.HasValue && (createdVal == null || createdVal > end.Value)) continue;
                    }

                    var fullName = $"{r.FirstName ?? ""} {(r.MiddleName ?? "")} {r.LastName ?? ""} {(r.Extension ?? "")}".Trim();
                    var dob = r.GetType().GetProperty("DateOfBirth", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)?.GetValue(r) as DateTime?;
                    rows.Add(new Dictionary<string, string>
                    {
                        ["Id"] = r.Id.ToString(),
                        ["FullName"] = fullName,
                        ["Role"] = r.Role ?? "",
                        ["DateOfBirth"] = dob?.ToString("yyyy-MM-dd") ?? "",
                        ["Sex"] = r.Sex ?? "",
                        ["Occupation"] = r.Occupation ?? "",
                        ["Education"] = r.Education ?? "",
                        ["HouseholdId"] = r.Household?.Id.ToString() ?? "",
                        ["Sitio"] = r.Household?.Sitio?.Name ?? ""
                    });
                }
            }
            else
            {
                columns = new List<string> { "Id" };
            }

            return (columns, rows);
        }

        // GET: /Bhw/GetReportData
        [HttpGet]
        public async Task<IActionResult> GetReportData(string reportType, DateTime? startDate, DateTime? endDate)
        {
            var (columns, rows) = await BuildReportData(reportType, startDate, endDate);
            return Json(new { columns, rows });
        }

        // GET: /Bhw/ExportCsv
        [HttpGet]
        public async Task<IActionResult> ExportCsv(string reportType, DateTime? startDate, DateTime? endDate)
        {
            var (columns, rows) = await BuildReportData(reportType, startDate, endDate);

            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", columns.Select(c => CsvEscape(c))));
            foreach (var r in rows)
            {
                var cells = columns.Select(c => CsvEscape(r.ContainsKey(c) ? r[c] : ""));
                sb.AppendLine(string.Join(",", cells));
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var name = $"{reportType}_{DateTime.UtcNow:yyyyMMddHHmmss}.csv";
            return File(bytes, "text/csv", name);

            static string CsvEscape(string input)
            {
                if (string.IsNullOrEmpty(input)) return "";
                if (input.Contains('"') || input.Contains(',') || input.Contains('\n'))
                    return $"\"{input.Replace("\"", "\"\"")}\"";
                return input;
            }
        }

        // GET: /Bhw/ExportExcel
        [HttpGet]
        public async Task<IActionResult> ExportExcel(string reportType, DateTime? startDate, DateTime? endDate)
        {
            var (columns, rows) = await BuildReportData(reportType, startDate, endDate);

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Report");
            for (int c = 0; c < columns.Count; c++)
            {
                ws.Cell(1, c + 1).Value = columns[c];
                ws.Cell(1, c + 1).Style.Font.Bold = true;
            }

            int rIdx = 2;
            foreach (var r in rows)
            {
                for (int c = 0; c < columns.Count; c++)
                {
                    var key = columns[c];
                    var cellValue = r.ContainsKey(key) ? r[key] ?? "" : "";
                    ws.Cell(rIdx, c + 1).SetValue(cellValue);
                }
                rIdx++;
            }

            ws.Columns().AdjustToContents();

            using var ms = new MemoryStream();
            workbook.SaveAs(ms);
            ms.Seek(0, SeekOrigin.Begin);
            var fileName = $"{reportType}_{DateTime.UtcNow:yyyyMMddHHmmss}.xlsx";
            return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        // GET: /Bhw/ExportPdf
        [HttpGet]
        public async Task<IActionResult> ExportPdf(string reportType, DateTime? startDate, DateTime? endDate)
        {
            var (columns, rows) = await BuildReportData(reportType, startDate, endDate);

            var generatedBy = User?.Identity?.Name ?? "system";
            var title = (reportType ?? "Report").Replace('-', ' ').ToUpperInvariant();

            var pdfBytes = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(20);
                    page.DefaultTextStyle(x => x.FontSize(10));

                    page.Header().Column(col =>
                    {
                        col.Item().Text(title).FontSize(16).SemiBold();
                        col.Item().Text($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}").FontSize(9);
                        col.Item().Text($"Generated by: {generatedBy}").FontSize(9);
                        col.Item().PaddingBottom(6).Text("");
                    });

                    page.Content().Element(content =>
                    {
                        if (columns == null || columns.Count == 0 || rows == null || rows.Count == 0)
                        {
                            content.Column(c =>
                            {
                                c.Item().Text("No data available for this report.").FontSize(10);
                            });
                            return;
                        }

                        content.Table(table =>
                        {
                            table.ColumnsDefinition(def =>
                            {
                                for (int i = 0; i < columns.Count; i++)
                                    def.RelativeColumn();
                            });

                            table.Header(header =>
                            {
                                foreach (var c in columns)
                                {
                                    header.Cell().Background(Colors.Grey.Lighten3).Padding(6).Border(1).BorderColor(Colors.Grey.Medium)
                                          .Text(c).SemiBold().FontSize(10);
                                }
                            });

                            foreach (var row in rows)
                            {
                                foreach (var c in columns)
                                {
                                    var txt = row.ContainsKey(c) ? (row[c] ?? "") : "";
                                    table.Cell().Padding(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten2)
                                         .Text(txt).FontSize(9);
                                }
                            }
                        });
                    });

                    page.Footer().AlignCenter().Text($"Generated by {generatedBy}").FontSize(9);
                });
            }).GeneratePdf();

            var fileName = $"{reportType}_{DateTime.UtcNow:yyyyMMddHHmmss}.pdf";
            return File(pdfBytes, "application/pdf", fileName);
        }

        // ---------- Residents action ----------
        [HttpGet]
        public async Task<IActionResult> Residents(string q = null)
        {
            await EnsureAssignedSitioInViewBagAsync();

            IQueryable<Resident> residentQ = _db.Residents
                .AsNoTracking()
                .Include(r => r.Household)
                .ThenInclude(h => h.Sitio);

            if (ViewBag.AssignedSitioId != null)
            {
                int sitioId = Convert.ToInt32(ViewBag.AssignedSitioId);
                residentQ = residentQ.Where(r => r.Household != null && r.Household.SitioId == sitioId);
            }

            residentQ = residentQ.Where(r => r.IsArchived == null || r.IsArchived == false);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLowerInvariant();
                residentQ = residentQ.Where(r =>
                    (r.FirstName ?? "").ToLower().Contains(term)
                    || (r.MiddleName ?? "").ToLower().Contains(term)
                    || (r.LastName ?? "").ToLower().Contains(term)
                    || (r.Occupation ?? "").ToLower().Contains(term)
                    || (r.Education ?? "").ToLower().Contains(term)
                    || r.Id.ToString().Contains(term)
                );
            }

            var rawList = await residentQ.ToListAsync();

            var list = rawList.Where(r =>
            {
                var t = r.GetType();

                var isArchived = t.GetProperty("IsArchived", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (isArchived != null)
                {
                    var val = isArchived.GetValue(r) as bool?;
                    if (val == true) return false;
                }

                var archivedAt = t.GetProperty("ArchivedAt", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                var deletedAt = t.GetProperty("DeletedAt", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                if (archivedAt != null && archivedAt.GetValue(r) != null) return false;
                if (deletedAt != null && deletedAt.GetValue(r) != null) return false;

                return true;
            })
            .Select(r => new ResidentListVm
            {
                Id = r.Id,
                HouseholdId = r.Household?.Id,
                Name = $"{r.FirstName} {r.MiddleName} {r.LastName}".Trim(),
                Sex = r.Sex,
                Occupation = r.Occupation,
                Education = r.Education,
                DateOfBirth = r.DateOfBirth,
                Age = r.Age ?? ComputeAge(r.DateOfBirth)
            })
            .ToList();

            ViewBag.SearchQuery = q ?? "";

            return View(list);
        }

        // Archive resident
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ArchiveResident(int id)
        {
            var resident = await _db.Residents.FindAsync(id);
            if (resident == null) return NotFound();

            try
            {
                resident.IsArchived = true;
                resident.ArchivedAt = DateTime.UtcNow;
                _db.Update(resident);
                await _db.SaveChangesAsync();

                TempData["SuccessMessage"] = "Resident archived.";
                return RedirectToAction(nameof(Residents));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ArchiveResident failed for id {Id}", id);
                TempData["ErrorMessage"] = "Failed to archive resident. Check logs.";
                return RedirectToAction(nameof(Residents));
            }
        }

        // Archived residents
        [HttpGet]
        public async Task<IActionResult> ArchivedResidents()
        {
            await EnsureAssignedSitioInViewBagAsync();

            IQueryable<Resident> residentQ = _db.Residents
                .AsNoTracking()
                .Include(r => r.Household)
                .ThenInclude(h => h.Sitio);

            if (ViewBag.AssignedSitioId != null)
            {
                int sitioId = Convert.ToInt32(ViewBag.AssignedSitioId);
                residentQ = residentQ.Where(r => r.Household != null && r.Household.SitioId == sitioId);
            }

            residentQ = residentQ.Where(r => r.IsArchived == true || r.ArchivedAt != null);

            var rawList = await residentQ.ToListAsync();

            var list = rawList.Where(r =>
            {
                var t = r.GetType();

                var isArchived = t.GetProperty("IsArchived", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (isArchived != null)
                {
                    var val = isArchived.GetValue(r) as bool?;
                    if (val == true) return true;
                }

                var archivedAt = t.GetProperty("ArchivedAt", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                var deletedAt = t.GetProperty("DeletedAt", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                if (archivedAt != null && archivedAt.GetValue(r) != null) return true;
                if (deletedAt != null && deletedAt.GetValue(r) != null) return true;

                return false;
            })
            .Select(r => new ResidentListVm
            {
                Id = r.Id,
                HouseholdId = r.Household?.Id,
                Name = $"{r.FirstName} {r.MiddleName} {r.LastName}".Trim(),
                Sex = r.Sex,
                Occupation = r.Occupation,
                Education = r.Education,
                DateOfBirth = r.DateOfBirth,
                Age = r.Age ?? ComputeAge(r.DateOfBirth)
            })
            .ToList();

            return View(list);
        }

        // Restore resident
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RestoreResident(int id)
        {
            var resident = await _db.Residents.FindAsync(id);
            if (resident == null) return NotFound();

            try
            {
                resident.IsArchived = false;
                resident.ArchivedAt = null;
                _db.Update(resident);
                await _db.SaveChangesAsync();
                TempData["SuccessMessage"] = "Resident restored.";
                return RedirectToAction(nameof(ArchivedResidents));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RestoreResident failed for id {Id}", id);
                TempData["ErrorMessage"] = "Failed to restore resident. Check logs.";
                return RedirectToAction(nameof(ArchivedResidents));
            }
        }

        // Settings pages
        [HttpGet]
        public async Task<IActionResult> Settings()
        {
            await EnsureAssignedSitioInViewBagAsync();
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Settings(ChangePasswordVm model)
        {
            await EnsureAssignedSitioInViewBagAsync();

            if (!ModelState.IsValid)
                return View(model);

            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                TempData["ErrorMessage"] = "Current user not found.";
                return View(model);
            }

            try
            {
                var result = await _userManager.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);

                if (!result.Succeeded)
                {
                    foreach (var err in result.Errors)
                        ModelState.AddModelError("", err.Description);

                    TempData["ErrorMessage"] = "Failed to change password.";
                    return View(model);
                }

                await _userManager.UpdateSecurityStampAsync(user);

                TempData["SuccessMessage"] = "Password changed successfully.";
                return RedirectToAction(nameof(Settings));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Change password failed for user {UserId}", user?.Id);
                TempData["ErrorMessage"] = "An unexpected error occurred.";
                return View(model);
            }
        }
    }
}
