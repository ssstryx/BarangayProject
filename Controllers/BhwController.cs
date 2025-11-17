using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BarangayProject.Data;
using BarangayProject.Models;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Text.Json;


namespace BarangayProject.Controllers
{
    [Authorize(Roles = "BHW")]
    public class BhwController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApplicationDbContext _db;

        public BhwController(UserManager<ApplicationUser> userManager, ApplicationDbContext db)
        {
            _userManager = userManager;
            _db = db;
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

        public async Task<IActionResult> Households()
        {
            // If Households DbSet exists return list, otherwise empty list
            var householdsQ = GetQueryableIfExists("Households", "Household", "Homes", "Families");
            if (householdsQ == null)
            {
                return View(Enumerable.Empty<Household>());
            }

            // Convert IQueryable<object> back to concrete list via provider
            var list = householdsQ.Provider.CreateQuery(householdsQ.Expression).Cast<Household>().ToList();
            return View(list);
        }

        public IActionResult Settings()
        {
            return View();
        }

        // ---------------- CRUD ----------------

        public async Task<IActionResult> ViewHousehold(int id)
        {
            var hh = await _db.Households.FindAsync(id);
            if (hh == null) return NotFound();
            return View(hh);
        }

        public async Task<IActionResult> EditHousehold(int id)
        {
            var hh = await _db.Households.FindAsync(id);
            if (hh == null) return NotFound();
            return View(hh);
        }

        [HttpPost]
        public async Task<IActionResult> DeleteHousehold(int id)
        {
            var hh = await _db.Households.FindAsync(id);
            if (hh == null) return NotFound();

            _db.Households.Remove(hh);
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] = "Household deleted.";
            return RedirectToAction(nameof(Households));
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
            if (!ModelState.IsValid)
            {
                return View(vm);
            }

            // Compose family head name (Father last/middle/first preferred or Mother if father blank)
            string familyHead = null;
            if (!string.IsNullOrWhiteSpace(vm.FatherFirstName) || !string.IsNullOrWhiteSpace(vm.FatherLastName))
            {
                familyHead = $"{vm.FatherFirstName} {vm.FatherMiddleName} {vm.FatherLastName} {vm.FatherExtension}".Replace("  ", " ").Trim();
            }
            if (string.IsNullOrWhiteSpace(familyHead))
            {
                // fallback to mother
                familyHead = $"{vm.MotherFirstName} {vm.MotherMiddleName} {vm.MotherLastName} {vm.MotherExtension}".Replace("  ", " ").Trim();
            }
            if (string.IsNullOrWhiteSpace(familyHead))
            {
                familyHead = "Unknown";
            }

            // Serialize full form to JSON and save in Details
            var detailsJson = JsonSerializer.Serialize(vm, new JsonSerializerOptions { WriteIndented = false });

            var household = new Household
            {
                FamilyHead = familyHead,
                Details = detailsJson
            };

            _db.Households.Add(household);
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] = "Household added successfully.";

            return RedirectToAction(nameof(Households));
        }
    }
    
}
