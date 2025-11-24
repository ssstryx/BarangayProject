// Controllers/BhwController.cs
using BarangayProject.Data;
using BarangayProject.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
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

        // ILogger injected correctly
        public BhwController(UserManager<ApplicationUser> userManager,ApplicationDbContext db,ILogger<BhwController> logger)
        {
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        // --- Helper: try to find a DbSet/IQueryable on the context by common names ---
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

            // try scanning all DbSet<> properties and match by element type name
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

        // --- Count an IQueryable<object> using Queryable.Count via reflection ---
        private int CountQueryable(IQueryable<object> source)
        {
            if (source == null) return 0;
            try
            {
                var elementType = source.ElementType;
                var countMethod = typeof(Queryable).GetMethods()
                    .First(m => m.Name == "Count" && m.GetParameters().Length == 1)
                    .MakeGenericMethod(elementType);

                // Invoke Count(source)
                var result = countMethod.Invoke(null, new object[] { source.Expression });
                return Convert.ToInt32(result);
            }
            catch
            {
                return 0;
            }
        }

        // --- Count where a string property equals a value (e.g. Gender == "Female") ---
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
            catch
            {
                return 0;
            }
        }

        // --- Count where a boolean property is true (e.g. IsMale == true) ---
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
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Build dashboard VM. This is defensive: if specific DbSets are missing the counts default to 0.
        /// </summary>
        private async Task<BhwDashboardVm> BuildDashboardVmAsync()
        {
            // fallback zeros
            int totalPopulation = 0, totalHouseholds = 0, totalFemale = 0, totalMale = 0;

            // Try common household set names
            var householdCandidates = new[] { "Households", "Household", "Homes", "Families" };
            var householdsQ = GetQueryableIfExists(householdCandidates);
            if (householdsQ != null)
            {
                totalHouseholds = CountQueryable(householdsQ);
            }
            else
            {
                // If there's no DbSet but you created a Household CLR type, we try _db.Set<Household>()
                try
                {
                    var hhType = typeof(Household);
                    var setMethod = _db.GetType().GetMethod("Set", Type.EmptyTypes);
                    if (setMethod != null)
                    {
                        var generic = setMethod.MakeGenericMethod(hhType);
                        var setValue = generic.Invoke(_db, null) as IQueryable;
                        if (setValue != null)
                        {
                            totalHouseholds = (int)typeof(Queryable).GetMethods().First(m => m.Name == "Count" && m.GetParameters().Length == 1)
                                .MakeGenericMethod(hhType).Invoke(null, new object[] { setValue.Expression });
                        }
                    }
                }
                catch { /* ignore */ }
            }

            // Try common person/resident set names
            var personsCandidates = new[] { "Residents", "People", "Persons", "Members", "UserProfiles", "ResidentsInfo" };
            var personsQ = GetQueryableIfExists(personsCandidates);

            if (personsQ != null)
            {
                totalPopulation = CountQueryable(personsQ);

                // try gender as string: Gender, Sex, GenderCode
                var genderNames = new[] { "Gender", "Sex", "GenderCode", "SexCode" };
                foreach (var g in genderNames)
                {
                    var f = CountWhereStringEquals(personsQ, g, "Female");
                    var m = CountWhereStringEquals(personsQ, g, "Male");
                    if (f + m > 0)
                    {
                        totalFemale = f;
                        totalMale = m;
                        break;
                    }

                    // try short codes "F"/"M"
                    f = CountWhereStringEquals(personsQ, g, "F");
                    m = CountWhereStringEquals(personsQ, g, "M");
                    if (f + m > 0)
                    {
                        totalFemale = f;
                        totalMale = m;
                        break;
                    }
                }

                // if still zero, try boolean property names like IsMale, Male
                if (totalMale == 0 && totalFemale == 0)
                {
                    var boolNames = new[] { "IsMale", "Male", "IsFemale" };
                    foreach (var b in boolNames)
                    {
                        var maleCount = CountWhereBoolTrue(personsQ, b);
                        if (maleCount > 0)
                        {
                            // If property indicates male true for males
                            totalMale = maleCount;
                            totalFemale = Math.Max(0, totalPopulation - totalMale);
                            break;
                        }

                        // If property indicates female true
                        var femaleCount = CountWhereBoolTrue(personsQ, b);
                        if (femaleCount > 0)
                        {
                            totalFemale = femaleCount;
                            totalMale = Math.Max(0, totalPopulation - totalFemale);
                            break;
                        }
                    }
                }
            }
            else
            {
                // No persons set found — attempt again with _db.Set<T> if a known type exists (not required)
                totalPopulation = 0;
                totalFemale = 0;
                totalMale = 0;
            }

            // Build VM
            var vm = new BhwDashboardVm
            {
                UserEmail = User.Identity?.Name ?? "",
                TotalPopulation = totalPopulation,
                TotalHouseholds = totalHouseholds,
                TotalFemale = totalFemale,
                TotalMale = totalMale
            };

            // Also expose to viewbag for legacy views
            ViewBag.TotalPopulation = vm.TotalPopulation;
            ViewBag.TotalHouseholds = vm.TotalHouseholds;
            ViewBag.TotalFemale = vm.TotalFemale;
            ViewBag.TotalMale = vm.TotalMale;

            return vm;
        }

        // ---------------- Pages ----------------

        public async Task<IActionResult> Index()
        {
            var vm = await BuildDashboardVmAsync();
            return View(vm);
        }

        // GET: list only non-archived households
        public async Task<IActionResult> Households()
        {
            var list = await _db.Households
                                .Where(h => !h.IsArchived)
                                .OrderBy(h => h.Id)
                                .ToListAsync();
            return View(list);
        }

        // POST: archive (soft-delete)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ArchiveHousehold(int id)
        {
            var hh = await _db.Households.FindAsync(id);
            if (hh == null) return NotFound();

            hh.IsArchived = true;
            hh.ArchivedAt = DateTime.UtcNow;
            hh.ArchivedBy = User?.Identity?.Name ?? "unknown";

            _db.Households.Update(hh);
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] = "Household archived.";
            return RedirectToAction(nameof(Households));
        }

        // POST: restore
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RestoreHousehold(int id)
        {
            var hh = await _db.Households.FindAsync(id);
            if (hh == null) return NotFound();

            hh.IsArchived = false;
            hh.ArchivedAt = null;
            hh.ArchivedBy = null;

            _db.Households.Update(hh);
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] = "Household restored.";
            return RedirectToAction(nameof(Households)); // or redirect to Archive list
        }

        public IActionResult Settings()
        {
            return View();
        }

        // GET: /Bhw/CreateHousehold
        [HttpGet]
        public IActionResult CreateHousehold()
        {
            var vm = new CreateHouseholdVm();
            // add one empty child by default for convenience (optional)
            vm.Children.Add(new ChildVm());
            return View(vm);
        }

        // POST: /Bhw/CreateHousehold
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateHousehold(CreateHouseholdVm vm)
        {
            // If ModelState invalid — serialize the errors into TempData for debugging.
            if (!ModelState.IsValid)
            {
                var ms = ModelState
                    .Where(kvp => kvp.Value.Errors.Count > 0)
                    .Select(kvp => new {
                        Key = kvp.Key,
                        Errors = kvp.Value.Errors.Select(e => string.IsNullOrEmpty(e.ErrorMessage) ? (e.Exception?.Message ?? "Exception with no message") : e.ErrorMessage).ToArray()
                    })
                    .ToArray();

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("ModelState is invalid. Errors:");
                foreach (var entry in ms)
                {
                    sb.AppendLine($" - {entry.Key}: {string.Join("; ", entry.Errors)}");
                }

                // Log and show to the user (debugging only)
                _logger.LogWarning("CreateHousehold: modelstate invalid: {Errors}", sb.ToString());
                TempData["ErrorMessage"] = sb.ToString();

                return View(vm);
            }

            string familyHead = null;
            if (!string.IsNullOrWhiteSpace(vm.FatherFirstName) || !string.IsNullOrWhiteSpace(vm.FatherLastName))
            {
                familyHead = $"{vm.FatherFirstName} {vm.FatherMiddleName} {vm.FatherLastName} {vm.FatherExtension}".Replace("  ", " ").Trim();
            }
            if (string.IsNullOrWhiteSpace(familyHead))
            {
                familyHead = $"{vm.MotherFirstName} {vm.MotherMiddleName} {vm.MotherLastName} {vm.MotherExtension}".Replace("  ", " ").Trim();
            }
            if (string.IsNullOrWhiteSpace(familyHead)) familyHead = "Unknown";

            var detailsJson = System.Text.Json.JsonSerializer.Serialize(vm, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });

            using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                var household = new Household
                {
                    FamilyHead = familyHead,
                    Details = detailsJson
                };

                _db.Households.Add(household);
                await _db.SaveChangesAsync(); // ensure household.Id

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
                        Sex = vm.FatherSex ?? "Male",
                        Occupation = occ ?? "",
                        OccupationOther = string.Equals(occ, vm.FatherOccupationOther, StringComparison.OrdinalIgnoreCase) ? vm.FatherOccupationOther : null,
                        Education = edu ?? "",
                        EducationOther = string.Equals(edu, vm.FatherEducationOther, StringComparison.OrdinalIgnoreCase) ? vm.FatherEducationOther : null
                    };

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
                        Sex = vm.MotherSex ?? "Female",
                        Occupation = occ ?? "",
                        OccupationOther = string.Equals(occ, vm.MotherOccupationOther, StringComparison.OrdinalIgnoreCase) ? vm.MotherOccupationOther : null,
                        Education = edu ?? "",
                        EducationOther = string.Equals(edu, vm.MotherEducationOther, StringComparison.OrdinalIgnoreCase) ? vm.MotherEducationOther : null
                    };

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
                            Sex = c.Sex ?? "",
                            Occupation = occ ?? "",
                            OccupationOther = string.Equals(occ, c.OccupationOther, StringComparison.OrdinalIgnoreCase) ? c.OccupationOther : null,
                            Education = edu ?? "",
                            EducationOther = string.Equals(edu, c.EducationOther, StringComparison.OrdinalIgnoreCase) ? c.EducationOther : null
                        };

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
                // Build a friendly error string including inner exceptions
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

                try { await tx.RollbackAsync(); } catch { /* ignore rollback errors */ }

                var full = GetFullMessage(ex);
                _logger.LogError(ex, "CreateHousehold failed: {Message}", full);

                // Put the full message into TempData so it shows on page (debugging only)
                TempData["ErrorMessage"] = "Error saving household: " + full;

                ModelState.AddModelError("", "Validation failed. Check server logs for details.");
                return View(vm);
            }
        }

    }
}
