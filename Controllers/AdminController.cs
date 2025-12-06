using BarangayProject.Data;
using BarangayProject.Models;
using BarangayProject.Models.AdminModel;
using BarangayProject.Services;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BarangayProject.Controllers
{
    [Authorize(Roles = "Admin")]
    // Controller: AdminController — handles web requests for admin
    public class AdminController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly AuditService _auditService;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<AdminController> _logger;
        private readonly BarangayProject.Services.IEmailSender _emailSender;

        // seeded admin we hide from lists
        private const string SystemAdminEmail = "barangayproject.mailer@gmail.com";

        // configuration keys
        private const string Config_SystemName = "SystemName";
        private const string Config_LogoPath = "LogoPath";
        private const string Config_SidebarCompact = "SidebarCompact";
        private const string Config_DashboardTheme = "DashboardTheme";
        private const string Config_MaintenanceMode = "MaintenanceMode";

        public AdminController(
            ApplicationDbContext db,
            UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole> roleManager,
            AuditService auditService,
            IWebHostEnvironment env,
            ILogger<AdminController> logger,
            BarangayProject.Services.IEmailSender emailSender)
        {
            _db = db;
            _userManager = userManager;
            _roleManager = roleManager;
            _audit_service_check(auditService);
            _auditService = auditService;
            _env = env;
            _logger = logger;
            _emailSender = emailSender;
        }

        // small helper to avoid analyzer warning (no-op)
        private void _role_manager_check(RoleManager<IdentityRole>? dummy) { /* no-op */ }
        private void _audit_service_check(AuditService? dummy) { /* no-op */ }

        // -------------------- Action filter: require email confirmation for admin actions --------------------
        public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            try
            {
                var action = (context.ActionDescriptor as Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor)?.ActionName ?? "";
                var allowEvenIfUnconfirmed = new[] { "ConfirmEmail", "SendVerificationEmail", "PromptVerifyEmail" };

                if (allowEvenIfUnconfirmed.Contains(action, StringComparer.OrdinalIgnoreCase))
                {
                    await base.OnActionExecutionAsync(context, next);
                    return;
                }

                if (!User.Identity?.IsAuthenticated ?? false)
                {
                    await base.OnActionExecutionAsync(context, next);
                    return;
                }

                var currentUser = await _userManager.GetUserAsync(User);
                if (currentUser == null)
                {
                    await base.OnActionExecutionAsync(context, next);
                    return;
                }

                if (!currentUser.EmailConfirmed)
                {
                    context.Result = RedirectToAction(nameof(PromptVerifyEmail));
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in email-confirmation enforcement; allowing request to continue.");
            }

            await base.OnActionExecutionAsync(context, next);
        }

        // -------------------- DASHBOARD --------------------
        public async Task<IActionResult> Index()
        {
            // totals (exclude seeded system admin)
            var totalUsers = await _db.Users
                .Where(u => u.Email != SystemAdminEmail)
                .CountAsync();

            var inactiveUsers = await _db.Users
                .Where(u => u.Email != SystemAdminEmail)
                .CountAsync(u => u.LockoutEnd.HasValue && u.LockoutEnd.Value > DateTimeOffset.UtcNow);

            var activeUsers = totalUsers - inactiveUsers;
            var totalSitios = await _db.Sitios.CountAsync();

            // --- Users by month (last 6 months inclusive of current month) ---
            var nowUtc = DateTime.UtcNow;
            var windowStart = new DateTime(nowUtc.Year, nowUtc.Month, 1).AddMonths(-5); // 6 months window start (first day)
            var windowEndInclusive = new DateTime(nowUtc.Year, nowUtc.Month, 1).AddMonths(1).AddTicks(-1);

            // Fetch created timestamps for users in the window (exclude system admin)
            var createdList = await _db.Users
                .Where(u => u.Email != SystemAdminEmail && u.CreatedAt != default && u.CreatedAt >= windowStart && u.CreatedAt <= windowEndInclusive)
                .Select(u => u.CreatedAt)
                .ToListAsync();

            var usersByMonth = new List<(string Month, int Count)>();
            for (int i = 0; i < 6; i++)
            {
                var month = windowStart.AddMonths(i);
                var label = month.ToString("MMM yyyy");
                var count = createdList.Count(dt => dt.Year == month.Year && dt.Month == month.Month);
                usersByMonth.Add((label, count));
            }

            // --- Sitios assignment (Assigned / Unassigned) ---
            var assignedCount = await _db.Sitios.CountAsync(s => s.SitioBhws.Any());
            var unassignedCount = Math.Max(0, totalSitios - assignedCount);

            var sitiosByAssignment = new List<(string Label, int Count)>
    {
        ("Assigned", assignedCount),
        ("Unassigned", unassignedCount)
    };

            // --- Users by Role (count non-system-admin users per role) ---
            var roles = await _db.Roles.OrderBy(r => r.Name).ToListAsync();
            var roleCounts = new List<(string Role, int Count)>();

            if (roles.Any())
            {
                // get user-role pairs joined to users (exclude system admin email)
                var userRolePairs = await _db.UserRoles
                    .Join(_db.Users,
                          ur => ur.UserId,
                          u => u.Id,
                          (ur, u) => new { ur.UserId, ur.RoleId, u.Email })
                    .Where(x => x.Email != SystemAdminEmail)
                    .ToListAsync();

                var countsByRoleId = userRolePairs
                    .GroupBy(x => x.RoleId)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.UserId).Distinct().Count());

                foreach (var r in roles)
                {
                    countsByRoleId.TryGetValue(r.Id, out var c);
                    roleCounts.Add((r.Name ?? "-", c));
                }
            }

            // build VM
            var vm = new DashboardViewModel
            {
                TotalUsers = totalUsers,
                ActiveUsers = activeUsers,
                InactiveUsers = inactiveUsers,
                TotalSitios = totalSitios,

                UsersByMonth = usersByMonth,
                SitiosByAssignment = sitiosByAssignment,
                RolesByCount = roleCounts
            };

            return View(vm);
        }



        [HttpPost]
        [ValidateAntiForgeryToken]
        // Short: ToggleDeactivate — async Task<IActionResult> action
        public async Task<IActionResult> ToggleDeactivate(string id, string userId)
        {
            // accept either parameter name (form uses "id", some links may use "userId")
            var uid = !string.IsNullOrWhiteSpace(userId) ? userId : id;
            if (string.IsNullOrWhiteSpace(uid))
            {
                TempData["ErrorMessage"] = "Invalid user id.";
                return RedirectToAction(nameof(ManageUsers));
            }

            var user = await _userManager.FindByIdAsync(uid);
            if (user == null)
            {
                TempData["ErrorMessage"] = "User not found.";
                return RedirectToAction(nameof(ManageUsers));
            }

            var isLocked = user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTimeOffset.UtcNow;
            if (isLocked)
            {
                user.LockoutEnd = null;
                user.LockoutEnabled = false;
            }
            else
            {
                user.LockoutEnd = DateTimeOffset.UtcNow.AddYears(100);
                user.LockoutEnabled = true;
            }

            var res = await _userManager.UpdateAsync(user);
            if (!res.Succeeded)
            {
                TempData["ErrorMessage"] = "Failed to update user lockout: " + string.Join(" | ", res.Errors.Select(e => e.Description));
                return RedirectToAction(nameof(ManageUsers));
            }

            await _auditService.AddAsync(
                action: isLocked ? "ActivateUser" : "DeactivateUser",
                details: $"{(isLocked ? "Activated" : "Deactivated")} user {GetFriendlyUserLabel(user)}",
                performedByUserId: _userManager.GetUserId(User),
                entityType: "User",
                entityId: user.Id);

            TempData["SuccessMessage"] = isLocked ? "User activated." : "User deactivated.";
            return RedirectToAction(nameof(ManageUsers));
        }

        // -------------------- MANAGE USERS --------------------
        public async Task<IActionResult> ManageUsers(string? search)
        {
            var q = _db.Users
                .Include(u => u.Profile)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var low = search.Trim().ToLower();
                var numeric = int.TryParse(low, out var num);

                q = q.Where(u =>
                    (u.Email != null && u.Email.ToLower().Contains(low)) ||
                    (u.DisplayName != null && u.DisplayName.ToLower().Contains(low)) ||
                    (u.UserName != null && u.UserName.ToLower().Contains(low)) ||
                    (u.Profile != null && (
                        (u.Profile.FirstName != null && u.Profile.FirstName.ToLower().Contains(low)) ||
                        (u.Profile.LastName != null && u.Profile.LastName.ToLower().Contains(low))
                    )) ||
                    (numeric && u.Profile != null && u.Profile.UserNumber == num) ||
                    (u.Id != null && u.Id.ToLower().Contains(low))
                );
            }

            var usersQuery = q
                .OrderBy(u => u.Email == SystemAdminEmail ? 0 : 1)
                .ThenBy(u => u.Profile != null && u.Profile.UserNumber != null ? u.Profile.UserNumber : int.MaxValue)
                .ThenBy(u => u.DisplayName ?? u.UserName);

            var users = await usersQuery.ToListAsync();

            var list = new List<ManageUserVm>(users.Count);
            foreach (var u in users)
            {
                var profile = u.Profile;
                var roles = await _userManager.GetRolesAsync(u);
                var role = roles.FirstOrDefault() ?? "-";
                var isLocked = u.LockoutEnabled && u.LockoutEnd.HasValue && u.LockoutEnd.Value > DateTimeOffset.UtcNow;

                list.Add(new ManageUserVm
                {
                    UserId = u.Id,
                    UserNumber = profile?.UserNumber,
                    Email = u.Email,
                    FullName = profile != null ? $"{profile.FirstName} {(string.IsNullOrWhiteSpace(profile.MiddleName) ? "" : profile.MiddleName + " ")}{profile.LastName}".Trim() : (u.DisplayName ?? u.UserName),
                    Role = role,
                    IsLockedOut = isLocked,
                    Joined = profile?.CreatedAt ?? (u.CreatedAt == default ? (DateTime?)null : u.CreatedAt)
                });
            }

            ViewBag.CurrentSearch = search ?? "";
            ViewBag.SystemAdminEmail = SystemAdminEmail;
            return View(list);
        }

        // ---------- Add User (GET + POST) ----------
        [HttpGet]
        public async Task<IActionResult> AddUser()
        {
            // pass roles for selector
            var roles = await _role_manager_getnames();
            ViewBag.Roles = roles;
            return View(new BarangayProject.Models.AddUserVm());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        // Short: AddUser — async Task<IActionResult> action
        public async Task<IActionResult> AddUser([FromForm] BarangayProject.Models.AddUserVm model)
        {
            // model is the class in Models/AddUserVm.cs (your strongly-typed model)
            if (!ModelState.IsValid)
            {
                ViewBag.Roles = await _role_manager_getnames();
                return View(model);
            }

            // check duplicates
            if (await _userManager.FindByEmailAsync(model.Email) != null)
            {
                ModelState.AddModelError(nameof(model.Email), "A user with that email already exists.");
                ViewBag.Roles = await _role_manager_getnames();
                return View(model);
            }

            var user = new ApplicationUser
            {
                UserName = model.Email,
                Email = model.Email,
                DisplayName = (!string.IsNullOrWhiteSpace(model.FirstName) || !string.IsNullOrWhiteSpace(model.LastName))
                ? $"{model.FirstName} {model.MiddleName ?? ""} {model.LastName}".Trim()
                : model.Email,
                CreatedAt = DateTime.UtcNow
            };

            var createRes = await _userManager.CreateAsync(user, model.Password);
            if (!createRes.Succeeded)
            {
                var errs = string.Join(" | ", createRes.Errors.Select(e => e.Description));
                ModelState.AddModelError("", "Failed to create user: " + errs);
                ViewBag.Roles = await _role_manager_getnames();
                return View(model);
            }

            // ... after createRes.Succeeded
            // ensure account is unconfirmed and locked until email confirmation
            user.EmailConfirmed = false; // usually default, but set explicitly
            user.LockoutEnabled = true;
            user.LockoutEnd = DateTimeOffset.UtcNow.AddYears(100); // effectively locked until confirmed
            await _userManager.UpdateAsync(user);

            // add role (unchanged)
            if (!string.IsNullOrWhiteSpace(model.Role))
            {
                var roleOk = await _roleManager.RoleExistsAsync(model.Role);
                if (roleOk)
                {
                    await _userManager.AddToRoleAsync(user, model.Role);
                }
            }

            // create profile row if you have UserProfile entity
            try
            {
                var profile = new UserProfile
                {
                    UserId = user.Id,
                    FirstName = model.FirstName ?? "",
                    MiddleName = model.MiddleName,
                    LastName = model.LastName ?? "",
                    CreatedAt = DateTime.UtcNow
                };
                _db.UserProfiles.Add(profile);
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to create profile row for new user (non-fatal).");
            }

            // send confirmation email so the user can verify from any device
            try
            {
                await SendConfirmationEmailAsync(user);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send confirmation email to new user {Email}", user.Email);
                // Decide whether to treat this as fatal. We'll show warning but still keep user created/locked.
                TempData["WarningMessage"] = "User created but verification email failed to send. Please check SMTP settings.";
            }

            await _auditService.AddAsync(
                action: "CreateUser",
                details: $"Created user {GetFriendlyUserLabel(user)} (email verification required)",
                performedByUserId: _userManager.GetUserId(User),
                entityType: "User",
                entityId: user.Id);

            // show helpful message: created + email sent (or warning)
            TempData["SuccessMessage"] = "User created. A verification email was sent; the user must confirm email before signing in.";
            return RedirectToAction(nameof(ManageUsers));


            // add role
            if (!string.IsNullOrWhiteSpace(model.Role))
            {
                var roleOk = await _roleManager.RoleExistsAsync(model.Role);
                if (roleOk)
                {
                    await _userManager.AddToRoleAsync(user, model.Role);
                }
            }

            // create profile row if you have UserProfile entity
            try
            {
                var profile = new UserProfile
                {
                    UserId = user.Id,
                    FirstName = model.FirstName ?? "",
                    MiddleName = model.MiddleName,
                    LastName = model.LastName ?? "",
                    CreatedAt = DateTime.UtcNow
                };
                _db.UserProfiles.Add(profile);
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to create profile row for new user (non-fatal).");
            }

            await _auditService.AddAsync(
                action: "CreateUser",
                details: $"Created user {GetFriendlyUserLabel(user)}",
                performedByUserId: _userManager.GetUserId(User),
                entityType: "User",
                entityId: user.Id);

            TempData["SuccessMessage"] = "User created.";
            return RedirectToAction(nameof(ManageUsers));
        }

        [HttpGet]
        // Short: EditUser — async Task<IActionResult> action
        public async Task<IActionResult> EditUser(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return BadRequest();
            var user = await _db.Users.Include(u => u.Profile).FirstOrDefaultAsync(u => u.Id == id);
            if (user == null) return NotFound();

            var roles = await _userManager.GetRolesAsync(user);
            var allRoles = await _db.Roles.OrderBy(r => r.Name).Select(r => r.Name).ToListAsync();

            var vm = new EditUserVm
            {
                UserId = user.Id,
                Email = user.Email,
                DisplayName = user.DisplayName,
                FirstName = user.Profile?.FirstName,
                LastName = user.Profile?.LastName,
                Role = roles.FirstOrDefault() ?? ""
            };

            ViewBag.AllRoles = allRoles;
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        // Short: EditUser — async Task<IActionResult> action
        public async Task<IActionResult> EditUser(EditUserVm model)
        {
            if (!ModelState.IsValid)
            {
                ViewBag.AllRoles = await _db.Roles.OrderBy(r => r.Name).Select(r => r.Name).ToListAsync();
                return View(model);
            }

            var user = await _userManager.FindByIdAsync(model.UserId);
            if (user == null) { TempData["ErrorMessage"] = "User not found."; return RedirectToAction(nameof(ManageUsers)); }

            // update profile row if present
            var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == user.Id);
            if (profile == null)
            {
                profile = new UserProfile { UserId = user.Id, CreatedAt = DateTime.UtcNow };
                _db.UserProfiles.Add(profile);
            }
            profile.FirstName = model.FirstName ?? "";
            profile.LastName = model.LastName ?? "";
            profile.ModifiedAt = DateTime.UtcNow;

            // update user fields
            user.Email = model.Email?.Trim();
            user.UserName = model.Email?.Trim();
            user.DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? user.DisplayName : model.DisplayName.Trim();

            var upd = await _userManager.UpdateAsync(user);
            if (!upd.Succeeded)
            {
                TempData["ErrorMessage"] = "Failed to update user: " + string.Join(" | ", upd.Errors.Select(e => e.Description));
                return RedirectToAction(nameof(ManageUsers));
            }

            // update role if changed (single-role example)
            if (!string.IsNullOrWhiteSpace(model.Role))
            {
                var currentRoles = await _userManager.GetRolesAsync(user);
                if (!currentRoles.Contains(model.Role))
                {
                    await _userManager.RemoveFromRolesAsync(user, currentRoles);
                    if (await _roleManager.RoleExistsAsync(model.Role))
                        await _userManager.AddToRoleAsync(user, model.Role);
                }
            }

            await _db.SaveChangesAsync();

            await _auditService.AddAsync(
                action: "EditUser",
                details: $"Edited user {GetFriendlyUserLabel(user)}",
                performedByUserId: _userManager.GetUserId(User),
                entityType: "User",
                entityId: user.Id);

            TempData["SuccessMessage"] = "User updated.";
            return RedirectToAction(nameof(ManageUsers));
        }

        // helper to get role names list (for views)
        private async Task<List<string>> _role_manager_getnames()
        {
            return await _db.Roles.OrderBy(r => r.Name).Select(r => r.Name).ToListAsync();
        }

        // -------------------- SITIO --------------------
        public async Task<IActionResult> ManageSitios(string? search)
        {
            var q = _db.Sitios
                .Include(s => s.SitioBhws)
                    .ThenInclude(sb => sb.Bhw)
                        .ThenInclude(u => u.Profile)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var low = search.Trim().ToLower();
                q = q.Where(s =>
                    s.Name.ToLower().Contains(low) ||
                    s.SitioBhws.Any(sb =>
                        (sb.Bhw != null) &&
                        (
                            (sb.Bhw.Profile != null && (sb.Bhw.Profile.FirstName ?? "").ToLower().Contains(low)) ||
                            (sb.Bhw.Profile != null && (sb.Bhw.Profile.LastName ?? "").ToLower().Contains(low)) ||
                            ((sb.Bhw.DisplayName ?? sb.Bhw.UserName ?? "").ToLower().Contains(low))
                        )
                    )
                );
            }

            var list = await q.OrderBy(s => s.Id).ToListAsync();
            ViewBag.CurrentSearch = search ?? "";
            return View(list);
        }

        [HttpGet]
        // Short: CreateSitio — IActionResult action
        public IActionResult CreateSitio()
        {
            return View(new Sitio());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        // Short: CreateSitio — async Task<IActionResult> action
        public async Task<IActionResult> CreateSitio(Sitio model)
        {
            if (!ModelState.IsValid) return View(model);

            if (await _db.Sitios.AnyAsync(s => s.Name == model.Name))
            {
                ModelState.AddModelError(nameof(model.Name), "A sitio with that name already exists.");
                return View(model);
            }

            var sitio = new Sitio { Name = model.Name };
            _db.Sitios.Add(sitio);
            await _db.SaveChangesAsync();

            // optional legacy AssignedBhwId handling
            string? singleAssignedBhwId = null;
            try
            {
                singleAssignedBhwId = model.GetType().GetProperty("AssignedBhwId")?.GetValue(model) as string;
            }
            catch
            {
                singleAssignedBhwId = null;
            }

            if (!string.IsNullOrWhiteSpace(singleAssignedBhwId))
            {
                var exists = await _db.SitioBhws.AnyAsync(sb => sb.SitioId == sitio.Id && sb.BhwId == singleAssignedBhwId);
                if (!exists)
                {
                    _db.SitioBhws.Add(new SitioBhw { SitioId = sitio.Id, BhwId = singleAssignedBhwId });
                    await _db.SaveChangesAsync();
                }
            }

            await _auditService.AddAsync(
                action: "CreateSitio",
                details: $"Created sitio '{sitio.Name}'",
                performedByUserId: _userManager.GetUserId(User),
                entityType: "Sitio",
                entityId: sitio.Id.ToString());

            TempData["SuccessMessage"] = "Sitio added.";
            return RedirectToAction(nameof(ManageSitios));
        }

        [HttpGet]
        // Short: EditSitio — async Task<IActionResult> action
        public async Task<IActionResult> EditSitio(int id)
        {
            var sitio = await _db.Sitios.Include(s => s.SitioBhws).FirstOrDefaultAsync(s => s.Id == id);
            if (sitio == null) return NotFound();
            return View(sitio);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        // Short: EditSitio — async Task<IActionResult> action
        public async Task<IActionResult> EditSitio(int id, Sitio model)
        {
            if (id != model.Id) return BadRequest();
            if (!ModelState.IsValid) return View(model);

            var sitio = await _db.Sitios.FindAsync(id);
            if (sitio == null) return NotFound();

            if (await _db.Sitios.AnyAsync(s => s.Id != id && s.Name == model.Name))
            {
                ModelState.AddModelError(nameof(model.Name), "Another sitio has the same name.");
                return View(model);
            }

            sitio.Name = model.Name;
            await _db.SaveChangesAsync();

            await _auditService.AddAsync(
                action: "EditSitio",
                details: $"Edited sitio '{sitio.Name}'",
                performedByUserId: _userManager.GetUserId(User),
                entityType: "Sitio",
                entityId: sitio.Id.ToString());

            TempData["SuccessMessage"] = "Sitio updated.";
            return RedirectToAction(nameof(ManageSitios));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        // Short: DeleteSitio — async Task<IActionResult> action
        public async Task<IActionResult> DeleteSitio(int? id)
        {
            if (!id.HasValue)
            {
                TempData["ErrorMessage"] = "Invalid sitio id.";
                return RedirectToAction(nameof(ManageSitios));
            }

            var sitio = await _db.Sitios.FindAsync(id.Value);
            if (sitio == null)
            {
                TempData["ErrorMessage"] = "Sitio not found.";
                return RedirectToAction(nameof(ManageSitios));
            }

            var assigned = await _db.UserProfiles.AnyAsync(p => p.SitioId == id.Value);
            if (assigned)
            {
                TempData["ErrorMessage"] = "Cannot delete sitio: there are users assigned to it. Reassign or remove them first.";
                return RedirectToAction(nameof(ManageSitios));
            }

            var sitioName = sitio.Name;
            var sitioId = sitio.Id;
            var joins = _db.SitioBhws.Where(sb => sb.SitioId == sitioId);
            if (joins.Any()) _db.SitioBhws.RemoveRange(joins);
            _db.Sitios.Remove(sitio);
            await _db.SaveChangesAsync();

            await _auditService.AddAsync(
                action: "DeleteSitio",
                details: $"Deleted sitio '{sitioName}'",
                performedByUserId: _userManager.GetUserId(User),
                entityType: "Sitio",
                entityId: sitioId.ToString());

            TempData["SuccessMessage"] = "Sitio deleted.";
            return RedirectToAction(nameof(ManageSitios));
        }

        // -------------------- ASSIGN BHWs (Many-to-many) --------------------
        private async Task<List<SelectListItem>> BuildBhwSelectListAsync(IEnumerable<string>? preSelected = null, int? currentSitioId = null)
        {
            preSelected ??= Enumerable.Empty<string>();

            var bhwRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == "BHW" || r.NormalizedName == "BHW");
            if (bhwRole == null) return new List<SelectListItem>();

            var bhwUserIds = await _db.UserRoles
                .Where(ur => ur.RoleId == bhwRole.Id)
                .Select(ur => ur.UserId)
                .ToListAsync();

            if (!bhwUserIds.Any()) return new List<SelectListItem>();

            // find all BHW ids that are already assigned to any sitio (we will exclude them from the available list)
            var assignedList = await _db.SitioBhws
                .Where(sb => sb.BhwId != null)
                .Select(sb => sb.BhwId)
                .Distinct()
                .ToListAsync();

            var assigned = new HashSet<string>(assignedList ?? new List<string>());

            // load candidate users and then filter out those assigned anywhere
            var bhwUsers = await _db.Users
                .Include(u => u.Profile)
                .Where(u => bhwUserIds.Contains(u.Id))
                .OrderBy(u => u.Profile != null && u.Profile.UserNumber != null ? u.Profile.UserNumber : int.MaxValue)
                .ThenBy(u => u.DisplayName ?? u.UserName)
                .ToListAsync();

            var list = new List<SelectListItem>();
            foreach (var u in bhwUsers)
            {
                // skip any that are assigned (including those assigned to this sitio)
                if (assigned.Contains(u.Id))
                    continue;

                string display;
                if (u.Profile != null)
                {
                    var first = (u.Profile.FirstName ?? "").Trim();
                    var last = (u.Profile.LastName ?? "").Trim();
                    var fullname = (first + " " + last).Trim();
                    display = !string.IsNullOrWhiteSpace(fullname) ? fullname : (string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName : u.DisplayName);
                }
                else
                {
                    display = string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName : u.DisplayName;
                }

                list.Add(new SelectListItem
                {
                    Text = display,
                    Value = u.Id,
                    Selected = false
                });
            }

            return list;
        }

        // GET: AssignBhw (display multi-select)
        [HttpGet]
        public async Task<IActionResult> AssignBhw(int sitioId)
        {
            var sitio = await _db.Sitios
                .Include(s => s.SitioBhws)
                    .ThenInclude(sb => sb.Bhw)
                        .ThenInclude(u => u.Profile)
                .FirstOrDefaultAsync(s => s.Id == sitioId);

            if (sitio == null) return NotFound();

            var selected = sitio.SitioBhws.Select(sb => sb.BhwId).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();

            // build label map for currently assigned BHWs
            var selectedLabels = new Dictionary<string, string>();
            if (selected.Any())
            {
                var users = await _db.Users
                    .Include(u => u.Profile)
                    .Where(u => selected.Contains(u.Id))
                    .ToListAsync();

                foreach (var u in users)
                {
                    selectedLabels[u.Id] = GetFriendlyUserLabel(u);
                }

                // for any id not found in Users (unlikely), include the id as fallback
                foreach (var id in selected)
                {
                    if (!selectedLabels.ContainsKey(id))
                        selectedLabels[id] = id;
                }
            }

            var vm = new AssignBhwVm
            {
                SitioId = sitio.Id,
                SitioName = sitio.Name ?? "",
                // pass sitioId so BuildBhwSelectListAsync can exclude bhws already assigned to other sitios
                AvailableBHWs = await BuildBhwSelectListAsync(selected, sitio.Id),
                SelectedBhwIds = selected,
                SelectedBhwLabels = selectedLabels
            };

            return View(vm);
        }

        // POST: AssignBhw
        [HttpPost]
        [ValidateAntiForgeryToken]
        // Short: AssignBhw — async Task<IActionResult> action
        public async Task<IActionResult> AssignBhw(int sitioId, List<string> selectedBhwIds)
        {
            var sitio = await _db.Sitios.Include(s => s.SitioBhws).FirstOrDefaultAsync(s => s.Id == sitioId);
            if (sitio == null) return NotFound();

            selectedBhwIds = (selectedBhwIds ?? new List<string>()).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();

            // Remove assignments that are no longer selected
            var toRemove = sitio.SitioBhws.Where(sb => !selectedBhwIds.Contains(sb.BhwId)).ToList();
            if (toRemove.Any()) _db.SitioBhws.RemoveRange(toRemove);

            // Add new assignments - but double-check the BHW isn't already assigned to some other sitio
            var existingIds = sitio.SitioBhws.Select(sb => sb.BhwId).ToHashSet();

            // fetch BHW ids already assigned to other sitios (safest approach for EF versions)
            var assignedElsewhereList = await _db.SitioBhws
                .Where(sb => sb.SitioId != sitioId)
                .Select(sb => sb.BhwId)
                .Distinct()
                .ToListAsync();

            var assignedElsewhere = new HashSet<string>(assignedElsewhereList ?? new List<string>());

            var toAdd = selectedBhwIds
                .Where(id => !existingIds.Contains(id) && !assignedElsewhere.Contains(id))
                .Select(id => new SitioBhw { SitioId = sitioId, BhwId = id })
                .ToList();

            if (toAdd.Any()) await _db.SitioBhws.AddRangeAsync(toAdd);

            await _db.SaveChangesAsync();

            await _auditService.AddAsync(
                action: "AssignBhw",
                details: $"Updated BHW assignments for sitio '{sitio.Name}' (assigned {selectedBhwIds.Count} BHW(s))",
                performedByUserId: _userManager.GetUserId(User),
                entityType: "Sitio",
                entityId: sitioId.ToString());

            TempData["SuccessMessage"] = "Assignments updated.";
            return RedirectToAction(nameof(ManageSitios));
        }

        // Remove a single SitioBhw join row (invoked by your "Remove" button)
        [HttpPost]
        [ValidateAntiForgeryToken]
        // Short: RemoveAssignedBhw — async Task<IActionResult> action
        public async Task<IActionResult> RemoveAssignedBhw(int sitioId, string bhwId)
        {
            if (sitioId <= 0 || string.IsNullOrWhiteSpace(bhwId))
            {
                TempData["ErrorMessage"] = "Invalid parameters.";
                return RedirectToAction(nameof(AssignBhw), new { sitioId });
            }

            var join = await _db.SitioBhws.FirstOrDefaultAsync(sb => sb.SitioId == sitioId && sb.BhwId == bhwId);
            if (join == null)
            {
                TempData["ErrorMessage"] = "Assignment not found.";
                return RedirectToAction(nameof(AssignBhw), new { sitioId });
            }

            _db.SitioBhws.Remove(join);
            await _db.SaveChangesAsync();

            var bhwUser = await _db.Users.Include(u => u.Profile).FirstOrDefaultAsync(u => u.Id == bhwId);
            var bhwLabel = bhwUser != null ? GetFriendlyUserLabel(bhwUser) : bhwId;

            await _auditService.AddAsync(
                action: "RemoveAssignedBhw",
                details: $"Removed BHW {bhwLabel} from sitio (Id: {sitioId})",
                performedByUserId: _userManager.GetUserId(User),
                entityType: "Sitio",
                entityId: sitioId.ToString());

            TempData["SuccessMessage"] = "Assignment removed.";
            return RedirectToAction(nameof(AssignBhw), new { sitioId = sitioId });
        }

        // -------------------- ASSIGN SITIO SELECT (NEW) --------------------
        [HttpGet]
        public async Task<IActionResult> AssignSitioSelect()
        {
            var sitios = await _db.Sitios.OrderBy(s => s.Name).Select(s => new SelectListItem { Value = s.Id.ToString(), Text = s.Name }).ToListAsync();
            var vm = new AssignSitioVm { Sitios = sitios };
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        // Short: AssignSitioSelect — IActionResult action
        public IActionResult AssignSitioSelect(int sitioId)
        {
            if (sitioId <= 0)
            {
                TempData["ErrorMessage"] = "Please select a sitio to assign.";
                return RedirectToAction(nameof(AssignSitioSelect));
            }

            return RedirectToAction(nameof(AssignBhw), new { sitioId = sitioId });
        }

        // -------------------- PRIVATE HELPERS --------------------
        private async Task PopulateBhwDropdown(string? selectedId = null)
        {
            var bhwRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == "BHW" || r.NormalizedName == "BHW");
            var bhwUsersList = new List<ApplicationUser>();

            // collect all assigned BHW ids so we can exclude them
            var assignedIds = await _db.SitioBhws
                .Select(sb => sb.BhwId)
                .Distinct()
                .ToListAsync();

            if (bhwRole != null)
            {
                var bhwUserIds = await _db.UserRoles
                    .Where(ur => ur.RoleId == bhwRole.Id)
                    .Select(ur => ur.UserId)
                    .ToListAsync();

                if (bhwUserIds.Any())
                {
                    bhwUsersList = await _db.Users
                        .Include(u => u.Profile)
                        .Where(u => bhwUserIds.Contains(u.Id)
                                    && !assignedIds.Contains(u.Id)) // exclude all already assigned BHWs
                        .OrderBy(u => u.Profile != null && u.Profile.UserNumber != null ? u.Profile.UserNumber : int.MaxValue)
                        .ThenBy(u => u.DisplayName ?? u.UserName)
                        .ToListAsync();
                }
            }

            var bhwSelectList = bhwUsersList
                .Select(u =>
                {
                    string display;
                    if (u.Profile != null)
                    {
                        var first = (u.Profile.FirstName ?? "").Trim();
                        var last = (u.Profile.LastName ?? "").Trim();
                        var fullname = (first + " " + last).Trim();
                        display = !string.IsNullOrWhiteSpace(fullname) ? fullname : (string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName : u.DisplayName);
                    }
                    else
                    {
                        display = string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName : u.DisplayName;
                    }
                    return new { u.Id, Display = display };
                })
                .ToList();

            ViewBag.BHWs = new SelectList(bhwSelectList, "Id", "Display", selectedId);
            ViewBag.BHWCount = bhwSelectList.Count;
            ViewBag.BHWList = bhwSelectList;
        }

        private IQueryable<ApplicationUser> BuildFilteredUsersQuery(
            string? search,
            DateTime? fromUtc,
            DateTime? toUtc,
            string? role = null,
            int? sitioId = null)
        {
            var q = _db.Users.Include(u => u.Profile).AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var low = search.Trim().ToLower();
                var numeric = int.TryParse(low, out var num);

                q = q.Where(u =>
                    (u.Email != null && u.Email.ToLower().Contains(low)) ||
                    (u.DisplayName != null && u.DisplayName.ToLower().Contains(low)) ||
                    (u.UserName != null && u.UserName.ToLower().Contains(low)) ||
                    (u.Profile != null && (
                        (u.Profile.FirstName != null && u.Profile.FirstName.ToLower().Contains(low)) ||
                        (u.Profile.LastName != null && u.Profile.LastName.ToLower().Contains(low))
                    )) ||
                    (numeric && u.Profile != null && u.Profile.UserNumber == num) ||
                    (u.Id != null && u.Id.ToLower().Contains(low))
                );
            }

            if (fromUtc.HasValue)
            {
                q = q.Where(u => u.CreatedAt != default && u.CreatedAt >= fromUtc.Value);
            }

            if (toUtc.HasValue)
            {
                q = q.Where(u => u.CreatedAt != default && u.CreatedAt <= toUtc.Value);
            }

            if (!string.IsNullOrWhiteSpace(role))
            {
                var roleEntity = _db.Roles.FirstOrDefault(r => r.Name == role || r.NormalizedName == role.ToUpper());
                if (roleEntity != null)
                {
                    var userIdsInRole = _db.UserRoles.Where(ur => ur.RoleId == roleEntity.Id).Select(ur => ur.UserId);
                    q = q.Where(u => userIdsInRole.Contains(u.Id));
                }
                else
                {
                    q = q.Where(u => false);
                }
            }

            if (sitioId.HasValue)
            {
                q = q.Where(u => u.Profile != null && u.Profile.SitioId == sitioId.Value);
            }

            return q;
        }

        //---------------Reports--------------------
        public async Task<IActionResult> Reports()
        {
            var vm = new ReportsViewModel();
            vm.TotalUsers = await _db.Users.CountAsync();
            vm.InactiveUsers = await _db.Users.CountAsync(u => u.LockoutEnd.HasValue && u.LockoutEnd.Value > DateTimeOffset.UtcNow);
            vm.ActiveUsers = vm.TotalUsers - vm.InactiveUsers;
            vm.TotalSitios = await _db.Sitios.CountAsync();

            var recentAudit = await _db.AuditLogs.OrderByDescending(a => a.EventTime).Take(30).ToListAsync();

            var referencedUserIds = recentAudit
                .Where(a => string.Equals(a.EntityType, "User", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(a.EntityId))
                .Select(a => a.EntityId).Distinct().ToList();

            var userProfilesMap = new Dictionary<string, (int? UserNumber, string DisplayName)>();
            if (referencedUserIds.Any())
            {
                var users = await _db.Users.Include(u => u.Profile).Where(u => referencedUserIds.Contains(u.Id)).ToListAsync();
                foreach (var u in users) userProfilesMap[u.Id] = (u.Profile?.UserNumber, u.Profile != null ? ((u.Profile.FirstName ?? "") + " " + (u.Profile.LastName ?? "")).Trim() : (u.DisplayName ?? u.UserName ?? ""));
            }

            var sitioIds = recentAudit
                .Where(a => string.Equals(a.EntityType, "Sitio", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(a.EntityId))
                .Select(a => a.EntityId).Distinct().ToList();

            var sitioMap = new Dictionary<string, Sitio>();
            if (sitioIds.Any())
            {
                var numericIds = sitioIds.Where(s => int.TryParse(s, out _)).Select(int.Parse).ToList();
                if (numericIds.Any())
                {
                    var sitios = await _db.Sitios.Where(s => numericIds.Contains(s.Id)).ToListAsync();
                    foreach (var s in sitios) sitioMap[s.Id.ToString()] = s;
                }
            }

            var dedup = recentAudit.GroupBy(a => new { a.Action, a.Details })
                .Select(g => g.OrderByDescending(x => x.EventTime).First())
                .OrderByDescending(x => x.EventTime)
                .Take(20)
                .ToList();

            var now = DateTime.UtcNow;
            var from = now.AddMonths(-5);

            var usersByMonth = await _db.Users
                .Where(u => u.CreatedAt != default && u.CreatedAt >= from)
                .ToListAsync();

            var groups = usersByMonth
                .GroupBy(u => new { Year = u.CreatedAt.Year, Month = u.CreatedAt.Month })
                .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Count() })
                .ToList();

            vm.UsersByMonth = new List<(string, int)>();
            for (int i = 0; i < 6; i++)
            {
                var dt = from.AddMonths(i);
                var grp = groups.FirstOrDefault(g => g.Year == dt.Year && g.Month == dt.Month);
                vm.UsersByMonth.Add((dt.ToString("MMM yyyy"), grp?.Count ?? 0));
            }

            var totalSitios = await _db.Sitios.CountAsync();
            var assigned = await _db.Sitios.CountAsync(s => s.SitioBhws.Any());
            vm.SitiosByAssignment = new List<(string, int)> { ("Assigned", assigned), ("Unassigned", totalSitios - assigned) };

            return View(vm);
        }

        // -------------------- New reporting endpoints (JSON + Exports) --------------------
        [HttpGet]
        public async Task<IActionResult> GetReportData(string reportType = "users", string? startDate = null, string? endDate = null)
        {
            try
            {
                DateTime? from = TryParseDate(startDate);
                DateTime? to = TryParseDate(endDate);

                var (columns, rows) = await BuildReportData(reportType, from, to);
                return Json(new { columns, rows });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetReportData failed");
                return StatusCode(500, new { error = "Failed to build report data." });
            }
        }

        [HttpGet]
        public async Task<IActionResult> ExportReportCsv(string reportType = "users", string? startDate = null, string? endDate = null)
        {
            try
            {
                DateTime? from = TryParseDate(startDate);
                DateTime? to = TryParseDate(endDate);
                var (columns, rows) = await BuildReportData(reportType, from, to);
                var csv = GenerateCsv(columns, rows);
                var bytes = Encoding.UTF8.GetBytes(csv);
                var fileName = $"report-{reportType}-{DateTime.UtcNow:yyyyMMdd}.csv";
                return File(bytes, "text/csv", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExportReportCsv failed");
                TempData["ErrorMessage"] = "Failed to export CSV.";
                return RedirectToAction(nameof(Reports));
            }
        }

        [HttpGet]
        public async Task<IActionResult> ExportReportExcel(string reportType = "users", string? startDate = null, string? endDate = null)
        {
            try
            {
                DateTime? from = TryParseDate(startDate);
                DateTime? to = TryParseDate(endDate);
                var (columns, rows) = await BuildReportData(reportType, from, to);
                var bytes = GenerateExcelBytes(columns, rows, $"Report - {reportType}");
                var fileName = $"report-{reportType}-{DateTime.UtcNow:yyyyMMdd}.xlsx";
                return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExportReportExcel failed");
                TempData["ErrorMessage"] = "Failed to export Excel.";
                return RedirectToAction(nameof(Reports));
            }
        }

        [HttpGet]
        public async Task<IActionResult> ExportReportPdf(string reportType = "users", string? startDate = null, string? endDate = null)
        {
            try
            {
                DateTime? from = TryParseDate(startDate);
                DateTime? to = TryParseDate(endDate);
                var (columns, rows) = await BuildReportData(reportType, from, to);
                var bytes = GeneratePdfBytes(columns, rows, $"Report - {reportType}");
                var fileName = $"report-{reportType}-{DateTime.UtcNow:yyyyMMdd}.pdf";
                return File(bytes, "application/pdf", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExportReportPdf failed");
                TempData["ErrorMessage"] = "Failed to export PDF.";
                return RedirectToAction(nameof(Reports));
            }
        }

        private async Task<(List<string> columns, List<Dictionary<string, object>> rows)> BuildReportData(string reportType, DateTime? fromUtc, DateTime? toUtc)
        {
            reportType = (reportType ?? "users").Trim().ToLowerInvariant();
            var columns = new List<string>();
            var rows = new List<Dictionary<string, object>>();

            if (reportType == "sitios")
            {
                columns = new List<string> { "Sitio Id", "Name", "AssignedUsers", "CreatedAt" };

                var q = _db.Sitios.AsQueryable();

                if (fromUtc.HasValue) q = q.Where(s => s.CreatedAt != default && s.CreatedAt >= fromUtc.Value);
                if (toUtc.HasValue) q = q.Where(s => s.CreatedAt != default && s.CreatedAt <= toUtc.Value);

                var sitiosList = await q
                    .Include(s => s.SitioBhws)
                    .OrderBy(s => s.Id)
                    .ToListAsync();

                foreach (var sitio in sitiosList)
                {
                    var dict = new Dictionary<string, object>
                    {
                        ["Sitio Id"] = sitio.Id,
                        ["Name"] = sitio.Name ?? "",
                        ["AssignedUsers"] = sitio.SitioBhws?.Count() ?? 0,
                        ["CreatedAt"] = (sitio.CreatedAt == default ? "" : sitio.CreatedAt.ToString("yyyy-MM-dd"))
                    };
                    rows.Add(dict);
                }

                return (columns, rows);
            }
            else // users
            {
                // --- users branch (fixed to show numeric user number like ManageUsers) ---
                columns = new List<string> { "User Number", "Full Name", "Role", "Sitio", "RegisteredAt", "Status" };

                var q = _db.Users.Include(u => u.Profile).AsQueryable();
                if (fromUtc.HasValue) q = q.Where(u => u.CreatedAt != default && u.CreatedAt >= fromUtc.Value);
                if (toUtc.HasValue) q = q.Where(u => u.CreatedAt != default && u.CreatedAt <= toUtc.Value);

                // exclude system admin by email
                q = q.Where(u => u.Email != SystemAdminEmail);

                var users = await q.OrderBy(u => u.Email).ToListAsync();

                // preload sitios and sitio-joins to avoid per-user DB hits
                var sitiosDict = await _db.Sitios.ToDictionaryAsync(s => s.Id, s => s.Name ?? "");
                var sitioBhws = await _db.SitioBhws.AsNoTracking().ToListAsync(); // join table: SitioId,BhwId

                // build fast lookup from bhw id -> sitio id (if any)
                var bhwToSitio = sitioBhws
                    .Where(sb => !string.IsNullOrWhiteSpace(sb.BhwId))
                    .GroupBy(sb => sb.BhwId)
                    .ToDictionary(g => g.Key!, g => g.Select(x => x.SitioId).FirstOrDefault());

                // counter to match Manage Users numbering (1,2,3...)
                int counter = 1;

                foreach (var u in users)
                {
                    var roles = await _userManager.GetRolesAsync(u);
                    var role = roles.FirstOrDefault() ?? "-";

                    // full name
                    string fullName;
                    if (u.Profile != null)
                    {
                        var first = (u.Profile.FirstName ?? "").Trim();
                        var last = (u.Profile.LastName ?? "").Trim();
                        fullName = (first + " " + last).Trim();
                        if (string.IsNullOrWhiteSpace(fullName))
                            fullName = u.DisplayName ?? u.UserName ?? "";
                    }
                    else
                    {
                        fullName = u.DisplayName ?? u.UserName ?? "";
                    }

                    // sitio: try Profile.SitioId first; if missing, try SitioBhws join for BHWs
                    string sitioName = "";
                    if (u.Profile != null)
                    {
                        int? sitioId = null;
                        var prop = u.Profile.GetType().GetProperty("SitioId");
                        if (prop != null)
                        {
                            var raw = prop.GetValue(u.Profile);
                            if (raw is int i) sitioId = i;
                            else
                            {
                                try { sitioId = Convert.ToInt32(raw); } catch { sitioId = null; }
                            }
                        }

                        if (sitioId.HasValue && sitioId.Value != 0)
                        {
                            sitiosDict.TryGetValue(sitioId.Value, out sitioName);
                            sitioName ??= "";
                        }
                    }

                    // fallback: if still empty, check SitioBhws mapping (BHW assignments)
                    if (string.IsNullOrWhiteSpace(sitioName) && !string.IsNullOrWhiteSpace(u.Id))
                    {
                        if (bhwToSitio.TryGetValue(u.Id, out var sId) && sId != 0)
                        {
                            sitiosDict.TryGetValue(sId, out sitioName);
                            sitioName ??= "";
                        }
                    }

                    var registeredAt = u.CreatedAt == default ? "" : u.CreatedAt.ToString("yyyy-MM-dd");
                    var status = (u.LockoutEnabled && u.LockoutEnd.HasValue && u.LockoutEnd.Value > DateTimeOffset.UtcNow) ? "Inactive" : "Active";

                    var dict = new Dictionary<string, object>
                    {
                        // Use generated counter so it matches Manage Users
                        ["User Number"] = counter,
                        ["Full Name"] = fullName,
                        ["Role"] = role,
                        ["Sitio"] = sitioName,
                        ["RegisteredAt"] = registeredAt,
                        ["Status"] = status,

                        // legacy keys for compatibility
                        ["User Id"] = u.Id ?? "",
                        ["SitioName"] = sitioName
                    };

                    rows.Add(dict);
                    counter++;
                }
            }
            var debugProfiles = await _db.UserProfiles.ToListAsync();
            foreach (var p in debugProfiles)
            {
                Console.WriteLine($"PROFILE: UserId={p.UserId}, UserNumber={p.UserNumber}, SitioId={p.SitioId}");
            }


            return (columns, rows);
        }


        private static DateTime? TryParseDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (DateTime.TryParse(raw, out var dt)) return dt;
            // Try strict yyyy-MM-dd
            if (DateTime.TryParseExact(raw, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt2))
                return dt2;
            return null;
        }

        private static string GenerateCsv(List<string> columns, List<Dictionary<string, object>> rows)
        {
            var sb = new StringBuilder();
            // header
            sb.AppendLine(string.Join(",", columns.Select(c => CsvEscape(c))));

            foreach (var r in rows)
            {
                var values = new List<string>();
                foreach (var c in columns)
                {
                    r.TryGetValue(c, out var val);
                    var text = val == null ? "" : val.ToString() ?? "";
                    values.Add(CsvEscape(text));
                }
                sb.AppendLine(string.Join(",", values));
            }

            return sb.ToString();

            static string CsvEscape(string s)
            {
                if (s == null) return "";
                var needsQuotes = s.Contains(",") || s.Contains("\"") || s.Contains("\n") || s.Contains("\r");
                var escaped = s.Replace("\"", "\"\"");
                return needsQuotes ? $"\"{escaped}\"" : escaped;
            }
        }

        private static byte[] GenerateExcelBytes(List<string> columns, List<Dictionary<string, object>> rows, string sheetName = "Report")
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add(string.IsNullOrWhiteSpace(sheetName) ? "Report" : sheetName);

            // header row
            for (int i = 0; i < columns.Count; i++)
            {
                ws.Cell(1, i + 1).Value = columns[i];
                ws.Cell(1, i + 1).Style.Font.Bold = true;
            }

            // rows
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                for (int c = 0; c < columns.Count; c++)
                {
                    row.TryGetValue(columns[c], out var val);
                    // use SetValue to avoid ClosedXML type conversion issues:
                    ws.Cell(r + 2, c + 1).SetValue(val?.ToString() ?? "");
                }
            }

            ws.Columns().AdjustToContents();

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }

        private static byte[] GeneratePdfBytes(List<string> columns, List<Dictionary<string, object>> rows, string title = "Report")
        {
            // Create a simple table PDF using QuestPDF
            var bytes = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Margin(20);
                    page.Size(PageSizes.A4);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontSize(10));

                    page.Header()
                        .PaddingBottom(10)
                        .Row(row =>
                        {
                            row.RelativeItem().Column(col =>
                            {
                                col.Item().Text(title).SemiBold().FontSize(14);
                                col.Item().Text($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} (UTC)").FontSize(9);
                            });
                        });

                    page.Content().PaddingVertical(5).Table(table =>
                    {
                        // define columns equally (simple approach)
                        for (int i = 0; i < columns.Count; i++)
                            table.ColumnsDefinition(column => column.RelativeColumn());

                        // header row
                        table.Header(header =>
                        {
                            foreach (var h in columns)
                            {
                                header.Cell().Background(Colors.Grey.Lighten3).Padding(5).Text(h).SemiBold().FontSize(9);
                            }
                        });

                        // data rows
                        foreach (var r in rows)
                        {
                            foreach (var c in columns)
                            {
                                r.TryGetValue(c, out var v);
                                var text = v == null ? "" : v.ToString() ?? "";
                                table.Cell().Padding(5).Text(text).FontSize(9);
                            }
                        }
                    });

                    page.Footer()
                        .AlignCenter()
                        .Text(x =>
                        {
                            x.Span("Barangay System");
                            x.Span(" • ").FontSize(9);
                            x.Span($"Page {{page}} of {{total}}").FontSize(9);
                        });
                });
            }).GeneratePdf();

            return bytes;
        }

        // -------------------- SETTINGS (GET & POST) --------------------
        [HttpGet]
        public async Task<IActionResult> Settings()
        {
            var vm = await GetSettingsVmAsync();
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        // Short: Settings — async Task<IActionResult> action
        public async Task<IActionResult> Settings(SettingsViewModel model)
        {
            try
            {
                var suppliedAction = (Request.Form["submitButton"].ToString() ?? "").Trim().ToLower();

                if (suppliedAction == "savesettings" || string.IsNullOrWhiteSpace(suppliedAction))
                {
                    if (!ModelState.IsValid)
                    {
                        var vmErr = await GetSettingsVmAsync();
                        vmErr.SystemName = model.SystemName ?? vmErr.SystemName;
                        vmErr.LogoPath = model.LogoPath ?? vmErr.LogoPath;

                        var allErrors = ModelState.Values.SelectMany(v => v.Errors)
                            .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? (e.Exception?.Message ?? "Unknown error") : e.ErrorMessage)
                            .ToList();

                        if (allErrors.Any()) TempData["ErrorMessage"] = "Validation error: " + string.Join(" | ", allErrors);
                        return View(vmErr);
                    }

                    var webroot = _env.WebRootPath;
                    if (string.IsNullOrWhiteSpace(webroot) || !Directory.Exists(webroot)) webroot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");

                    var imagesFolder = Path.Combine(webroot, "images");
                    Directory.CreateDirectory(imagesFolder);

                    async Task PersistLogoPathAsync(string storedValue)
                    {
                        await SetConfigValueAsync(Config_LogoPath, storedValue);
                        try
                        {
                            var cfgRow = await _db.SystemConfigurations.FirstOrDefaultAsync();
                            if (cfgRow != null)
                            {
                                cfgRow.LogoFileName = storedValue;
                                cfgRow.ModifiedAt = DateTime.UtcNow;
                                await _db.SaveChangesAsync();
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to update SystemConfigurations.LogoFileName (non-fatal).");
                        }
                    }

                    if (model.SiteLogoUpload != null && model.SiteLogoUpload.Length > 0)
                    {
                        try
                        {
                            var savedFileName = "logo.png";
                            var savePath = Path.Combine(imagesFolder, savedFileName);
                            using (var stream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                await model.SiteLogoUpload.CopyToAsync(stream);
                            }

                            if (!System.IO.File.Exists(savePath) || new FileInfo(savePath).Length == 0)
                            {
                                _logger.LogError("Logo file was written but does not exist or is empty at {path}", savePath);
                                TempData["ErrorMessage"] = "Upload completed but saved file is missing or empty.";
                                var vm = await GetSettingsVmAsync();
                                vm.SystemName = model.SystemName ?? vm.SystemName;
                                return View(vm);
                            }

                            var storedValue = "/images/" + savedFileName + "?v=" + DateTime.UtcNow.Ticks.ToString();
                            await PersistLogoPathAsync(storedValue);
                            _logger.LogInformation("Saved logo to {file} and persisted config value {val}", savePath, storedValue);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to write uploaded logo to {imagesFolder}", imagesFolder);
                            TempData["ErrorMessage"] = "Failed to save logo file. Check server file permissions and disk space.";
                            var vm = await GetSettingsVmAsync();
                            vm.SystemName = model.SystemName ?? vm.SystemName;
                            return View(vm);
                        }
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(model.LogoPath))
                        {
                            var candidate = model.LogoPath.Trim();
                            bool looksLikeFilenameOnly = !candidate.Contains('/') && !candidate.Contains('\\') && !candidate.Contains("://");
                            if (looksLikeFilenameOnly) candidate = "/images/" + candidate.TrimStart('/');
                            await PersistLogoPathAsync(candidate);
                            _logger.LogInformation("Persisted manual LogoPath value: {val}", candidate);
                        }
                    }

                    string CleanSystemName(string raw)
                    {
                        if (string.IsNullOrWhiteSpace(raw)) return "";
                        var tmp = raw.Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
                            .Replace("<br/>", " ", StringComparison.OrdinalIgnoreCase)
                            .Replace("<br />", " ", StringComparison.OrdinalIgnoreCase);

                        tmp = Regex.Replace(tmp, "<.*?>", " ");
                        tmp = Regex.Replace(tmp, @"\s+", " ").Trim();
                        return tmp;
                    }

                    var cleanedName = CleanSystemName(model.SystemName ?? "");
                    await SetConfigValueAsync(Config_SystemName, cleanedName);

                    var adminId = _userManager.GetUserId(User);
                    await _auditService.AddAsync(
                        action: "EditSettings",
                        details: $"Updated system settings: Name='{cleanedName}'",
                        performedByUserId: adminId,
                        entityType: "System",
                        entityId: adminId ?? "");

                    TempData["SuccessMessage"] = "Settings updated.";
                    return RedirectToAction(nameof(Settings));
                }

                if (suppliedAction == "changepassword")
                {
                    if (string.IsNullOrWhiteSpace(model.ResetNewPassword) || string.IsNullOrWhiteSpace(model.ResetNewPasswordConfirm) || model.ResetNewPassword != model.ResetNewPasswordConfirm)
                    {
                        TempData["ErrorMessage"] = "Password and confirmation must match and cannot be empty.";
                        return View(await GetSettingsVmAsync());
                    }

                    var pass = model.ResetNewPassword ?? "";
                    var meetsBasic = pass.Length >= 8 && Regex.IsMatch(pass, @"\d") && Regex.IsMatch(pass, @"[^\w\s]|_");
                    if (!meetsBasic)
                    {
                        TempData["ErrorMessage"] = "Password must be at least 8 characters and include at least one number and one symbol.";
                        return View(await GetSettingsVmAsync());
                    }

                    var targetUser = await _userManager.FindByIdAsync(model.ResetUserId);
                    if (targetUser == null)
                    {
                        TempData["ErrorMessage"] = "Selected user not found.";
                        return View(await GetSettingsVmAsync());
                    }

                    foreach (var validator in _userManager.PasswordValidators)
                    {
                        var validationResult = await validator.ValidateAsync(_userManager, targetUser, model.ResetNewPassword);
                        if (!validationResult.Succeeded)
                        {
                            var errs = string.Join(" | ", validationResult.Errors.Select(e => e.Description));
                            TempData["ErrorMessage"] = "Password validation failed: " + errs;
                            return View(await GetSettingsVmAsync());
                        }
                    }

                    var token = await _userManager.GeneratePasswordResetTokenAsync(targetUser);
                    var resetRes = await _userManager.ResetPasswordAsync(targetUser, token, model.ResetNewPassword);
                    if (!resetRes.Succeeded)
                    {
                        TempData["ErrorMessage"] = string.Join(" | ", resetRes.Errors.Select(e => e.Description));
                        return View(await GetSettingsVmAsync());
                    }

                    await _auditService.AddAsync(
                        action: "ResetPassword",
                        details: $"Reset password for user {GetFriendlyUserLabel(targetUser)}",
                        performedByUserId: _userManager.GetUserId(User),
                        entityType: "User",
                        entityId: targetUser.Id);

                    TempData["SuccessMessage"] = "Password reset successful.";
                    return RedirectToAction(nameof(Settings));
                }

                TempData["ErrorMessage"] = "Unknown action.";
                return RedirectToAction(nameof(Settings));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in Settings POST");
                TempData["ErrorMessage"] = "An unexpected error occurred while saving settings. Check server logs.";
                var vm = await GetSettingsVmAsync();
                vm.SystemName = model.SystemName ?? vm.SystemName;
                vm.LogoPath = model.LogoPath ?? vm.LogoPath;
                return View(vm);
            }
        }

        private async Task<SettingsViewModel> GetSettingsVmAsync()
        {
            var vm = new SettingsViewModel
            {
                SystemName = await GetConfigValueAsync(Config_SystemName) ?? "Barangay System",
                LogoPath = await GetConfigValueAsync(Config_LogoPath) ?? "/images/logo.png",
            };

            if (string.IsNullOrWhiteSpace(vm.LogoPath) || vm.LogoPath == "/images/logo.png")
            {
                try
                {
                    var cfgRow = await _db.SystemConfigurations.FirstOrDefaultAsync(sc => sc.SiteName == "LogoPath");
                    if (cfgRow != null && !string.IsNullOrWhiteSpace(cfgRow.LogoFileName))
                    {
                        vm.LogoPath = cfgRow.LogoFileName;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not read systemconfigurations for logo fallback.");
                }
            }

            return vm;
        }

        // -------------------- Flexible config helpers --------------------
        private static string DetectConfigKeyPropertyName()
        {
            var t = typeof(SystemConfiguration);
            var candidates = new[] { "Key", "Name", "ConfigKey", "SettingKey", "SettingName" };
            foreach (var c in candidates) if (t.GetProperty(c, BindingFlags.Public | BindingFlags.Instance) != null) return c;
            var fallback = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.PropertyType == typeof(string));
            return fallback?.Name ?? "Key";
        }

        private static string DetectConfigValuePropertyName()
        {
            var t = typeof(SystemConfiguration);
            var candidates = new[] { "Value", "ConfigValue", "SettingValue", "Val", "Setting", "LogoFileName", "LogoPath", "SiteName" };
            foreach (var c in candidates) if (t.GetProperty(c, BindingFlags.Public | BindingFlags.Instance) != null) return c;
            var keyName = DetectConfigKeyPropertyName();
            var fallback = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.PropertyType == typeof(string) && p.Name != keyName);
            return fallback?.Name ?? "Value";
        }

        private async Task<string?> GetConfigValueAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var keyPropName = DetectConfigKeyPropertyName();
            var valuePropName = DetectConfigValuePropertyName();

            var all = await _db.SystemConfigurations.AsNoTracking().ToListAsync();
            if (all == null || all.Count == 0) return null;

            var config = all.FirstOrDefault(c =>
            {
                var t = c.GetType();
                var keyProp = t.GetProperty(keyPropName, BindingFlags.Public | BindingFlags.Instance);
                var val = keyProp?.GetValue(c);
                return val != null && string.Equals(val.ToString()?.Trim(), key.Trim(), StringComparison.OrdinalIgnoreCase);
            });

            if (config == null) return null;

            var cfgType = config.GetType();
            var valueProp = cfgType.GetProperty(valuePropName, BindingFlags.Public | BindingFlags.Instance);
            var v = valueProp?.GetValue(config);
            return v?.ToString();
        }

        private async Task SetConfigValueAsync(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            var keyPropName = DetectConfigKeyPropertyName();
            var valuePropName = DetectConfigValuePropertyName();

            var all = await _db.SystemConfigurations.ToListAsync();
            var existing = all.FirstOrDefault(c =>
            {
                var t = c.GetType();
                var kp = t.GetProperty(keyPropName, BindingFlags.Public | BindingFlags.Instance);
                var kv = kp?.GetValue(c);
                return kv != null && string.Equals(kv.ToString()?.Trim(), key.Trim(), StringComparison.OrdinalIgnoreCase);
            });

            if (existing != null)
            {
                var t = existing.GetType();
                var valueProp = t.GetProperty(valuePropName, BindingFlags.Public | BindingFlags.Instance);
                var updatedAtProp = t.GetProperty("UpdatedAt", BindingFlags.Public | BindingFlags.Instance);
                valueProp?.SetValue(existing, value);
                updatedAtProp?.SetValue(existing, DateTime.UtcNow);
                _db.SystemConfigurations.Update(existing);
            }
            else
            {
                var newConfig = Activator.CreateInstance<SystemConfiguration>();
                var t = newConfig.GetType();
                var keyProp = t.GetProperty(keyPropName, BindingFlags.Public | BindingFlags.Instance);
                var valueProp = t.GetProperty(valuePropName, BindingFlags.Public | BindingFlags.Instance);
                var createdAt = t.GetProperty("CreatedAt", BindingFlags.Public | BindingFlags.Instance);
                keyProp?.SetValue(newConfig, key);
                valueProp?.SetValue(newConfig, value);
                createdAt?.SetValue(newConfig, DateTime.UtcNow);
                _db.SystemConfigurations.Add(newConfig);
            }

            await _db.SaveChangesAsync();
        }

        // -------------------- STATIC MAPPING HELPER --------------------
        private static string MapAuditToFriendlyText(
            AuditLog a,
            IReadOnlyDictionary<string, (int? UserNumber, string DisplayName)> userProfilesMap,
            IReadOnlyDictionary<string, Sitio> sitioMap)
        {
            var action = a.Action ?? "";
            var details = a.Details ?? "";

            static string ExtractLabelFromDetails(string details)
            {
                if (string.IsNullOrWhiteSpace(details)) return "";

                var emailMatch = Regex.Match(details, @"([a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,})");
                if (emailMatch.Success) return emailMatch.Value;

                var userNameMatch = Regex.Match(details, @"(?:user|user\s)([:\s]*)'?(?<n>[^'\(]+?)\s*(?:\(Id:|\bDeleted\b|\bSuccessfully\b|$)", RegexOptions.IgnoreCase);
                if (userNameMatch.Success)
                {
                    var n = userNameMatch.Groups["n"].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(n)) return n;
                }

                var sitioMatch = Regex.Match(details, @"(?:Created|Deleted|Edited)\s+sitio\s*'([^']+)'", RegexOptions.IgnoreCase);
                if (sitioMatch.Success) return sitioMatch.Groups[1].Value.Trim();

                var idMatch = Regex.Match(details, @"\b(Id:\s*([0-9]+)|([0-9a-fA-F\-]{8,}))\b");
                if (idMatch.Success)
                {
                    var g = idMatch.Groups[2].Value;
                    if (!string.IsNullOrWhiteSpace(g)) return $"(Id: {g})";
                    var tok = idMatch.Groups[3].Value;
                    return tok.Length >= 8 ? tok.Substring(0, 8) : tok;
                }

                return details.Length > 50 ? details.Substring(0, 50) + "..." : details;
            }

            if (!string.IsNullOrWhiteSpace(a.EntityType) && a.EntityType.Equals("User", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(a.EntityId))
            {
                if (userProfilesMap.TryGetValue(a.EntityId!, out var up))
                {
                    var userLabel = !string.IsNullOrWhiteSpace(up.DisplayName) ? up.DisplayName : (up.UserNumber.HasValue ? up.UserNumber.Value.ToString() : a.EntityId!.Substring(0, Math.Min(8, a.EntityId.Length)));
                    if (action.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0) return $"➕ New User Added: {userLabel}";
                    if (action.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0) return $"🗑️ Deleted user {userLabel}";
                    if (action.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0) return $"✏️ Edited user {userLabel}";
                    if (action.IndexOf("Deactivate", StringComparison.OrdinalIgnoreCase) >= 0) return $"🔒 Deactivated user {userLabel}";
                    if (action.IndexOf("Activate", StringComparison.OrdinalIgnoreCase) >= 0) return $"🔓 Activated user {userLabel}";
                    return $"{action}: {userLabel}{(string.IsNullOrWhiteSpace(details) ? "" : " - " + details)}";
                }
                else
                {
                    var extracted = ExtractLabelFromDetails(details);
                    if (!string.IsNullOrWhiteSpace(extracted))
                    {
                        if (action.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0) return $"➕ New User Added: {extracted}";
                        if (action.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0) return $"🗑️ Deleted user {extracted}";
                        if (action.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0) return $"✏️ Edited user {extracted}";
                        if (action.IndexOf("Deactivate", StringComparison.OrdinalIgnoreCase) >= 0) return $"🔒 Deactivated user {extracted}";
                        if (action.IndexOf("Activate", StringComparison.OrdinalIgnoreCase) >= 0) return $"🔓 Activated user {extracted}";
                        return $"{action}: {extracted}";
                    }

                    var shortId = a.EntityId!.Length >= 8 ? a.EntityId.Substring(0, 8) : a.EntityId;
                    return $"{action}: {shortId}{(string.IsNullOrWhiteSpace(details) ? "" : " - " + details)}";
                }
            }

            if (!string.IsNullOrWhiteSpace(a.EntityType) && a.EntityType.Equals("Sitio", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(a.EntityId))
            {
                if (sitioMap.TryGetValue(a.EntityId!, out var sitio))
                {
                    var label = $"'{sitio.Name}'";
                    if (action.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0) return $"➕ Created sitio {label}";
                    if (action.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0) return $"🗑️ Deleted sitio {label}";
                    if (action.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0) return $"✏️ Edited sitio {label}";
                    return $"{action}: {label}{(string.IsNullOrWhiteSpace(details) ? "" : " - " + details)}";
                }

                var nameMatch = Regex.Match(details, @"(?:Created|Deleted|Edited)\s+sitio\s*'([^']+)'", RegexOptions.IgnoreCase);
                if (nameMatch.Success)
                {
                    var nm = nameMatch.Groups[1].Value;
                    if (action.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0) return $"🗑️ Deleted sitio '{nm}'";
                    if (action.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0) return $"➕ Created sitio '{nm}'";
                    if (action.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0) return $"✏️ Edited sito '{nm}'";
                    return $"{action}: '{nm}'{(string.IsNullOrWhiteSpace(details) ? "" : " - " + details)}";
                }

                if (int.TryParse(a.EntityId, out var sid))
                {
                    if (action.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0) return $"🗑️ Deleted sitio (Id: {sid})";
                    if (action.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0) return $"➕ Created sitio (Id: {sid})";
                    return $"{action}: Sitio (Id: {sid}){(string.IsNullOrWhiteSpace(details) ? "" : " - " + details)}";
                }

                var shortId2 = a.EntityId.Length >= 8 ? a.EntityId.Substring(0, 8) : a.EntityId;
                return $"{action}: {shortId2}{(string.IsNullOrWhiteSpace(details) ? "" : " - " + details)}";
            }

            if (action.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0) return $"🗑️ {details}";
            if (action.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0) return $"➕ {details}";
            if (action.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0) return $"✏️ {details}";
            if (action.IndexOf("Deactivate", StringComparison.OrdinalIgnoreCase) >= 0) return $"🔒 {details}";
            if (action.IndexOf("Activate", StringComparison.OrdinalIgnoreCase) >= 0) return $"🔓 {details}";

            return $"{action}: {details}";
        }

        private string GetFriendlyUserLabel(ApplicationUser u)
        {
            if (u == null) return "";
            var profile = _db.UserProfiles.FirstOrDefault(p => p.UserId == u.Id);
            if (profile != null)
            {
                var full = ((profile.FirstName ?? "") + " " + (profile.LastName ?? "")).Trim();
                if (!string.IsNullOrWhiteSpace(full)) return full;
                if (profile.UserNumber.HasValue) return profile.UserNumber.Value.ToString();
            }

            if (!string.IsNullOrWhiteSpace(u.DisplayName)) return u.DisplayName;
            if (!string.IsNullOrWhiteSpace(u.Email)) return u.Email;
            return u.Id.Length >= 8 ? u.Id.Substring(0, 8) : u.Id;
        }

        [HttpGet]
        // Short: PromptVerifyEmail — IActionResult action
        public IActionResult PromptVerifyEmail()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        // Short: SendVerificationEmail — async Task<IActionResult> action
        public async Task<IActionResult> SendVerificationEmail()
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null) return Challenge();
            if (currentUser.EmailConfirmed)
            {
                TempData["SuccessMessage"] = "Email already verified.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                await SendConfirmationEmailAsync(currentUser);
                TempData["SuccessMessage"] = "Verification email sent. Check your inbox.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send verification email.");
                TempData["ErrorMessage"] = "Failed to send verification email. Check server logs / SMTP settings.";
            }

            return RedirectToAction(nameof(PromptVerifyEmail));
        }

        [HttpGet]
        [AllowAnonymous]
        // Short: ConfirmEmail — async Task<IActionResult> action
        public async Task<IActionResult> ConfirmEmail(string userId, string token)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(token))
            {
                TempData["ErrorMessage"] = "Invalid confirmation link.";
                return RedirectToAction(nameof(Index));
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                TempData["ErrorMessage"] = "User not found for this confirmation link.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                var result = await _userManager.ConfirmEmailAsync(user, token);
                if (result.Succeeded)
                {
                    // remove lockout so the newly-confirmed user can sign in
                    user.LockoutEnd = null;
                    user.LockoutEnabled = false;
                    await _userManager.UpdateAsync(user);

                    await _auditService.AddAsync(
                        action: "ConfirmEmail",
                        details: $"Email confirmed for user {GetFriendlyUserLabel(user)}",
                        performedByUserId: user.Id,
                        entityType: "User",
                        entityId: user.Id);

                    TempData["SuccessMessage"] = "Email address confirmed. You may now log in.";
                    return RedirectToAction(nameof(Index));
                }

                else
                {
                    var errors = string.Join(" | ", result.Errors.Select(e => e.Description));
                    TempData["ErrorMessage"] = "Failed to confirm email: " + errors;
                    return RedirectToAction(nameof(PromptVerifyEmail));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while confirming email.");
                TempData["ErrorMessage"] = "Error while confirming email. Try again or contact admin.";
                return RedirectToAction(nameof(PromptVerifyEmail));
            }
        }

        private async Task SendConfirmationEmailAsync(ApplicationUser user)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));
            if (string.IsNullOrWhiteSpace(user.Email)) throw new InvalidOperationException("User has no email.");

            var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            var confirmUrl = Url.Action(
                action: nameof(ConfirmEmail),
                controller: "Admin",
                values: new { userId = user.Id, token = token },
                protocol: Request.Scheme);

            if (string.IsNullOrWhiteSpace(confirmUrl))
            {
                var scheme = Request.Scheme ?? "https";
                var host = Request.Host.Value ?? "localhost";
                confirmUrl = $"{scheme}://{host}/Admin/ConfirmEmail?userId={WebUtility.UrlEncode(user.Id)}&token={WebUtility.UrlEncode(token)}";
            }

            var subject = "Please confirm your email";
            var html = $@"
                <p>Hello {(user.DisplayName ?? user.UserName ?? user.Email)},</p>
                <p>Please confirm your email for admin access by clicking the link below:</p>
                <p><a href='{confirmUrl}'>Confirm my email</a></p>
                <p>If the link doesn't work, copy-paste this URL into your browser:</p>
                <p>{confirmUrl}</p>
                <p>Thanks,<br/>Barangay System</p>
            ";

            await _emailSender.SendEmailAsync(user.Email, subject, html);

            await _auditService.AddAsync(
                action: "SendConfirmationEmail",
                details: $"Sent email confirmation to {user.Email}",
                performedByUserId: _userManager.GetUserId(User),
                entityType: "User",
                entityId: user.Id);
        }

        // helper used in AddUser
        private Task<bool> _role_manager_exists(string role) => _roleManager.RoleExistsAsync(role);
    } // end controller

    // Minimal view-model used by AssignBhw view
    public class AssignBhwVm
    {
        public int SitioId { get; set; }
        public string SitioName { get; set; } = "";
        public List<SelectListItem> AvailableBHWs { get; set; } = new();
        public List<string> SelectedBhwIds { get; set; } = new();
        public Dictionary<string, string> SelectedBhwLabels { get; set; } = new();
    }


    // View-model for AssignSitioSelect page
    public class AssignSitioVm
    {
        public List<SelectListItem> Sitios { get; set; } = new();
        public int SelectedSitioId { get; set; }
    }
}