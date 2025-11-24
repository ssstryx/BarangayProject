using BarangayProject.Data;
using BarangayProject.Models;
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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;


namespace BarangayProject.Controllers
{
    [Authorize(Roles = "Admin")]
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
        private const string Config_DashboardTheme = "DashboardTheme"; // left for compatibility (not used)
        private const string Config_MaintenanceMode = "MaintenanceMode"; // left for compatibility (not used)

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
            _auditService = auditService;
            _env = env;
            _logger = logger;
            _emailSender = emailSender;
        }


        // small helper to avoid analyzer warning (no-op)
        private void _role_manager_check(RoleManager<IdentityRole>? dummy) { /* no-op */ }

        // -------------------- Action filter: require email confirmation for admin actions --------------------
        // This prevents unconfirmed admin accounts from using admin UI. It allows
        // ConfirmEmail and SendVerificationEmail actions (so they can confirm/resend).
        public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            try
            {
                // allow anonymous/confirm actions to run without check
                var action = (context.ActionDescriptor as Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor)?.ActionName ?? "";

                // actions that should be allowed even if email not confirmed
                var allowEvenIfUnconfirmed = new[] { "ConfirmEmail", "SendVerificationEmail", "PromptVerifyEmail" };

                if (allowEvenIfUnconfirmed.Contains(action, StringComparer.OrdinalIgnoreCase))
                {
                    await base.OnActionExecutionAsync(context, next);
                    return;
                }

                // If user is not authenticated (shouldn't happen due to [Authorize]) just continue
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
                    // redirect to PromptVerifyEmail (GET) so they can resend confirmation
                    context.Result = RedirectToAction(nameof(PromptVerifyEmail));
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in email-confirmation enforcement; allowing request to continue.");
                // fall through to allow request rather than block on exceptions
            }

            await base.OnActionExecutionAsync(context, next);
        }

        // -------------------- DASHBOARD --------------------
        public async Task<IActionResult> Index()
        {
            var totalUsers = await _db.Users
                .Where(u => u.Email != SystemAdminEmail)
                .CountAsync();

            var inactiveUsers = await _db.Users
                .Where(u => u.Email != SystemAdminEmail)
                .CountAsync(u => u.LockoutEnd.HasValue && u.LockoutEnd.Value > DateTimeOffset.UtcNow);

            var activeUsers = totalUsers - inactiveUsers;
            var totalSitios = await _db.Sitios.CountAsync();

            var recentAuditEntities = await _db.AuditLogs
                .OrderByDescending(a => a.EventTime)
                .Take(40)
                .ToListAsync();

            var referencedUserIds = recentAuditEntities
                .Where(a => string.Equals(a.EntityType, "User", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(a.EntityId))
                .Select(a => a.EntityId!)
                .Distinct()
                .ToList();

            var referencedSitioIds = recentAuditEntities
                .Where(a => string.Equals(a.EntityType, "Sitio", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(a.EntityId))
                .Select(a => a.EntityId!)
                .Distinct()
                .ToList();

            var userProfilesMap = new Dictionary<string, (int? UserNumber, string DisplayName)>();
            if (referencedUserIds.Any())
            {
                var users = await _db.Users
                    .Include(u => u.Profile)
                    .Where(u => referencedUserIds.Contains(u.Id))
                    .ToListAsync();

                foreach (var u in users)
                {
                    int? userNumber = u.Profile?.UserNumber;
                    string display = u.Profile != null
                        ? ((u.Profile.FirstName ?? "") + " " + (u.Profile.LastName ?? "")).Trim()
                        : (u.DisplayName ?? u.Email ?? u.UserName ?? u.Id);

                    userProfilesMap[u.Id] = (userNumber, display);
                }
            }

            var sitioMap = new Dictionary<string, Sitio>();
            if (referencedSitioIds.Any())
            {
                var numericIds = referencedSitioIds
                    .Where(s => int.TryParse(s, out _))
                    .Select(int.Parse)
                    .ToList();

                if (numericIds.Any())
                {
                    var sitios = await _db.Sitios.Where(s => numericIds.Contains(s.Id)).ToListAsync();
                    foreach (var s in sitios) sitioMap[s.Id.ToString()] = s;
                }

                var remaining = referencedSitioIds.Except(sitioMap.Keys).ToList();
                if (remaining.Any())
                {
                    var found = await _db.Sitios.Where(s => remaining.Contains(s.Id.ToString())).ToListAsync();
                    foreach (var s in found) sitioMap[s.Id.ToString()] = s;
                }
            }

            var dedup = recentAuditEntities
                .GroupBy(a => new { a.Action, a.Details })
                .Select(g => g.OrderByDescending(x => x.EventTime).First())
                .OrderByDescending(x => x.EventTime)
                .Take(10)
                .ToList();

            var recentAudits = dedup
                .Select(a => new DashboardActivityVm
                {
                    Timestamp = a.EventTime,
                    Description = MapAuditToFriendlyText(a, userProfilesMap, sitioMap)
                })
                .OrderByDescending(x => x.Timestamp)
                .ToList();

            var model = new DashboardViewModel
            {
                TotalUsers = totalUsers,
                ActiveUsers = activeUsers,
                InactiveUsers = inactiveUsers,
                TotalSitios = totalSitios,
                RecentActivities = recentAudits
            };

            return View(model);
        }

        // -------------------- Clear Recent Activity --------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClearRecentActivity()
        {
            var logs = await _db.AuditLogs.ToListAsync();
            if (logs.Any())
            {
                _db.AuditLogs.RemoveRange(logs);
                await _db.SaveChangesAsync();
            }

            TempData["SuccessMessage"] = "Recent activity cleared.";
            return RedirectToAction(nameof(Index));
        }

        // -------------------- MANAGE USERS --------------------
        public async Task<IActionResult> ManageUsers(string? search)
        {
            var q = _db.Users
                       .Include(u => u.Profile)
                       .AsQueryable();

            // NOTE: Do NOT exclude SystemAdminEmail here - we want admin included in the list.
            // Allow optional filtering:
            if (!string.IsNullOrWhiteSpace(search))
            {
                var low = search.Trim().ToLower();
                var numeric = int.TryParse(low, out var num);

                q = q.Where(u =>
                    (u.Email != null && u.Email.ToLower().Contains(low)) ||
                    (u.DisplayName != null && u.DisplayName.ToLower().Contains(low)) ||
                    (u.UserName != null && u.UserName.ToLower().Contains(low)) ||
                    (u.Profile != null &&
                        (
                            (u.Profile.FirstName != null && u.Profile.FirstName.ToLower().Contains(low)) ||
                            (u.Profile.LastName != null && u.Profile.LastName.ToLower().Contains(low))
                        )
                    ) ||
                    (numeric && u.Profile != null && u.Profile.UserNumber == num) ||
                    (u.Id != null && u.Id.ToLower().Contains(low))
                );
            }

            // Ensure SystemAdminEmail appears first in the list
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
                    FullName = profile != null
                        ? $"{profile.FirstName} {(string.IsNullOrWhiteSpace(profile.MiddleName) ? "" : profile.MiddleName + " ")}{profile.LastName}".Trim()
                        : (u.DisplayName ?? u.UserName),
                    Role = role,
                    IsLockedOut = isLocked,
                    Joined = profile?.CreatedAt ?? (u.CreatedAt == default ? (DateTime?)null : u.CreatedAt)
                });
            }

            ViewBag.CurrentSearch = search ?? "";
            ViewBag.SystemAdminEmail = SystemAdminEmail; // expose to view so view can protect admin UI if desired
            return View(list);
        }

        // -------------------- EDIT USER --------------------
        [HttpGet]
        public async Task<IActionResult> EditUser(string id)
        {
            if (string.IsNullOrEmpty(id)) return NotFound();

            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == id);
            var roles = await _userManager.GetRolesAsync(user);
            var role = roles.FirstOrDefault() ?? "";

            var vm = new EditUserVm
            {
                UserId = user.Id,
                Email = user.Email,
                FirstName = profile?.FirstName,
                MiddleName = profile?.MiddleName,
                LastName = profile?.LastName,
                Role = role
            };

            ViewData["AvailableRoles"] = new[] { "Admin", "BNS", "BHW" };
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditUser(EditUserVm model)
        {
            if (!ModelState.IsValid)
            {
                ViewData["AvailableRoles"] = new[] { "Admin", "BNS", "BHW" };
                return View(model);
            }

            var user = await _userManager.FindByIdAsync(model.UserId);
            if (user == null) return NotFound();

            var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == user.Id);
            if (profile == null)
            {
                profile = new UserProfile
                {
                    UserId = user.Id,
                    FirstName = model.FirstName,
                    MiddleName = model.MiddleName,
                    LastName = model.LastName,
                    CreatedAt = DateTime.UtcNow
                };
                _db.UserProfiles.Add(profile);
            }
            else
            {
                profile.FirstName = model.FirstName;
                profile.MiddleName = model.MiddleName;
                profile.LastName = model.LastName;
            }

            var currentRoles = await _userManager.GetRolesAsync(user);
            foreach (var r in currentRoles) await _userManager.RemoveFromRoleAsync(user, r);

            if (!string.IsNullOrWhiteSpace(model.Role))
            {
                if (!await _roleManager.RoleExistsAsync(model.Role))
                    await _roleManager.CreateAsync(new IdentityRole(model.Role));

                await _userManager.AddToRoleAsync(user, model.Role);
            }

            await _db.SaveChangesAsync();

            await _auditService.AddAsync(
                action: "EditUser",
                details: $"Edited user {GetFriendlyUserLabel(user)}",
                performedByUserId: _userManager.GetUserId(User),
                entityType: "User",
                entityId: user.Id
            );

            TempData["SuccessMessage"] = "User updated successfully.";
            return RedirectToAction(nameof(ManageUsers));
        }

        // -------------------- ADD USER (GET) --------------------
        [HttpGet]
        public IActionResult AddUser()
        {
            ViewData["AvailableRoles"] = new[] { "Admin", "BNS", "BHW" };
            return View(new AddUserVm());
        }

        // -------------------- ADD USER (POST) - Modified to create unconfirmed user + send confirmation email --------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddUser(AddUserVm model)
        {
            ViewData["AvailableRoles"] = new[] { "Admin", "BNS", "BHW" };

            if (!ModelState.IsValid) return View(model);

            var existingUser = await _userManager.FindByEmailAsync(model.Email);
            if (existingUser != null)
            {
                ModelState.AddModelError(nameof(model.Email), "Email already exists.");
                return View(model);
            }

            var user = new ApplicationUser
            {
                UserName = model.Email,
                Email = model.Email,
                // set EmailConfirmed = false so user must verify
                EmailConfirmed = false,
                DisplayName = $"{model.FirstName} {model.LastName}".Trim()
            };

            var result = await _userManager.CreateAsync(user, model.Password);
            if (!result.Succeeded)
            {
                foreach (var e in result.Errors) ModelState.AddModelError(string.Empty, e.Description);
                return View(model);
            }

            if (!string.IsNullOrWhiteSpace(model.Role))
            {
                if (!await _roleManager.RoleExistsAsync(model.Role))
                    await _roleManager.CreateAsync(new IdentityRole(model.Role));

                await _userManager.AddToRoleAsync(user, model.Role);
            }

            try
            {
                int? nextNum = null;
                try
                {
                    var maxNum = await _db.UserProfiles.MaxAsync(p => (int?)p.UserNumber);
                    nextNum = (maxNum ?? 0) + 1;
                }
                catch { }

                var profile = new UserProfile
                {
                    UserId = user.Id,
                    FirstName = model.FirstName,
                    MiddleName = model.MiddleName,
                    LastName = model.LastName,
                    CreatedAt = DateTime.UtcNow,
                    UserNumber = nextNum
                };

                _db.UserProfiles.Add(profile);
                await _db.SaveChangesAsync();

                await _auditService.AddAsync(
                    action: "CreateUser",
                    details: $"Created user {GetFriendlyUserLabel(user)}",
                    performedByUserId: _userManager.GetUserId(User),
                    entityType: "User",
                    entityId: user.Id
                );
            }
            catch
            {
            }

            // Send confirmation email (best-effort)
            try
            {
                await SendConfirmationEmailAsync(user);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send confirmation email for new user {email}", user.Email);
                // don't fail the request; inform admin via TempData
                TempData["ErrorMessage"] = "User created but confirmation email failed to send. Check SMTP settings.";
            }

            TempData["SuccessMessage"] = "User added successfully. A confirmation email has been sent.";
            return RedirectToAction(nameof(ManageUsers));
        }

        // -------------------- ACTIVATE/DEACTIVATE USER --------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleDeactivate(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                TempData["ErrorMessage"] = "Invalid user ID.";
                return RedirectToAction(nameof(ManageUsers));
            }

            var user = await _userManager.FindByIdAsync(id);
            if (user == null)
            {
                TempData["ErrorMessage"] = "User not found.";
                return RedirectToAction(nameof(ManageUsers));
            }

            // Protect system admin from being toggled
            if (!string.IsNullOrWhiteSpace(user.Email) && user.Email.Equals(SystemAdminEmail, StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = "Action not allowed on system administrator account.";
                return RedirectToAction(nameof(ManageUsers));
            }

            var adminId = _userManager.GetUserId(User);

            // Determine current lockout state
            var isLocked = await _userManager.IsLockedOutAsync(user);

            try
            {
                if (isLocked)
                {
                    // === ACTIVATE ===
                    // Clear lockout end (null) and disable lockout
                    var endRes = await _user_manager_set_lockout_end(user, (DateTimeOffset?)null);
                    var enabledRes = await _user_manager_set_lockout_enabled(user, false);

                    var errors = new List<string>();
                    if (!endRes.Succeeded) errors.AddRange(endRes.Errors.Select(e => e.Description));
                    if (!enabledRes.Succeeded) errors.AddRange(enabledRes.Errors.Select(e => e.Description));

                    // Persist changes - some stores require UpdateAsync
                    IdentityResult updRes = IdentityResult.Success;
                    try
                    {
                        updRes = await _userManager.UpdateAsync(user);
                        if (!updRes.Succeeded) errors.AddRange(updRes.Errors.Select(e => e.Description));
                    }
                    catch
                    {
                        // swallow; we'll handle via errors list if needed
                    }

                    if (errors.Count > 0)
                    {
                        TempData["ErrorMessage"] = "Failed to activate user. " + string.Join("; ", errors);
                        return RedirectToAction(nameof(ManageUsers));
                    }

                    // immediate DB audit entry so RecentActivity shows activation right away
                    try
                    {
                        var audit = new AuditLog
                        {
                            EventTime = DateTime.UtcNow,
                            UserId = adminId ?? string.Empty,
                            Action = "ActivateUser",
                            Details = $"Activated user {GetFriendlyUserLabel(user)} (Id: {user.Id})",
                            EntityType = "User",
                            EntityId = user.Id,
                            CreatedAt = DateTime.UtcNow
                        };
                        _db.AuditLogs.Add(audit);
                        await _db.SaveChangesAsync();
                    }
                    catch
                    {
                        // ignore audit persistence errors
                    }

                    // still call AuditService for other sinks
                    await _auditService.AddAsync(
                        action: "ActivateUser",
                        details: $"Activated user {GetFriendlyUserLabel(user)} (Id: {user.Id})",
                        performedByUserId: adminId,
                        entityType: "User",
                        entityId: user.Id
                    );

                    TempData["SuccessMessage"] = "User activated successfully.";
                }
                else
                {
                    // === DEACTIVATE ===
                    var farFuture = DateTimeOffset.UtcNow.AddYears(100);

                    var enabledRes = await _userManager.SetLockoutEnabledAsync(user, true);
                    var endRes = await _userManager.SetLockoutEndDateAsync(user, farFuture);

                    var errors = new List<string>();
                    if (!enabledRes.Succeeded) errors.AddRange(enabledRes.Errors.Select(e => e.Description));
                    if (!endRes.Succeeded) errors.AddRange(endRes.Errors.Select(e => e.Description));

                    IdentityResult updRes = IdentityResult.Success;
                    try
                    {
                        updRes = await _userManager.UpdateAsync(user);
                        if (!updRes.Succeeded) errors.AddRange(updRes.Errors.Select(e => e.Description));
                    }
                    catch
                    {
                        // swallow
                    }

                    if (errors.Count > 0)
                    {
                        TempData["ErrorMessage"] = "Failed to deactivate user. " + string.Join("; ", errors);
                        return RedirectToAction(nameof(ManageUsers));
                    }

                    // immediate DB audit entry so RecentActivity shows deactivation right away
                    try
                    {
                        var audit = new AuditLog
                        {
                            EventTime = DateTime.UtcNow,
                            UserId = adminId ?? string.Empty,
                            Action = "DeactivateUser",
                            Details = $"Deactivated user {GetFriendlyUserLabel(user)} (Id: {user.Id})",
                            EntityType = "User",
                            EntityId = user.Id,
                            CreatedAt = DateTime.UtcNow
                        };
                        _db.AuditLogs.Add(audit);
                        await _db.SaveChangesAsync();
                    }
                    catch
                    {
                    }

                    await _auditService.AddAsync(
                        action: "DeactivateUser",
                        details: $"Deactivated user {GetFriendlyUserLabel(user)} (Id: {user.Id})",
                        performedByUserId: adminId,
                        entityType: "User",
                        entityId: user.Id
                    );

                    TempData["SuccessMessage"] = "User deactivated successfully.";
                }
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Server error: " + ex.Message;
            }

            return RedirectToAction(nameof(ManageUsers));
        }

        // small compatibility wrappers for possible provider differences
        private async Task<IdentityResult> _user_manager_set_lockout_end(ApplicationUser user, DateTimeOffset? end)
        {
            try { return await _userManager.SetLockoutEndDateAsync(user, end); }
            catch { return IdentityResult.Success; }
        }
        private async Task<IdentityResult> _user_manager_set_lockout_enabled(ApplicationUser user, bool enabled)
        {
            try { return await _userManager.SetLockoutEnabledAsync(user, enabled); }
            catch { return IdentityResult.Success; }
        }

        // -------------------- RESET USER PASSWORD (DIRECT, NO EMAIL) --------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetUserPasswordDirect(string userId, string newPassword, string confirmPassword)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                TempData["ErrorMessage"] = "Missing user ID.";
                return RedirectToAction(nameof(ManageUsers));
            }

            if (string.IsNullOrWhiteSpace(newPassword) || string.IsNullOrWhiteSpace(confirmPassword) || newPassword != confirmPassword)
            {
                TempData["ErrorMessage"] = "Password and confirmation must match.";
                return RedirectToAction(nameof(ManageUsers));
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                TempData["ErrorMessage"] = "User not found.";
                return RedirectToAction(nameof(ManageUsers));
            }

            // Validate password using Identity validators
            foreach (var validator in _userManager.PasswordValidators)
            {
                var result = await validator.ValidateAsync(_userManager, user, newPassword);
                if (!result.Succeeded)
                {
                    TempData["ErrorMessage"] = string.Join(" | ", result.Errors.Select(e => e.Description));
                    return RedirectToAction(nameof(ManageUsers));
                }
            }

            // Perform reset using token
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var reset = await _userManager.ResetPasswordAsync(user, token, newPassword);

            if (!reset.Succeeded)
            {
                TempData["ErrorMessage"] = string.Join(" | ", reset.Errors.Select(e => e.Description));
                return RedirectToAction(nameof(ManageUsers));
            }

            // Unlock user if they were locked
            if (await _userManager.IsLockedOutAsync(user))
            {
                await _userManager.SetLockoutEndDateAsync(user, null);
                await _userManager.SetLockoutEnabledAsync(user, false);
            }

            // Audit
            await _auditService.AddAsync(
                action: "ResetPasswordByAdmin",
                details: $"Admin reset password for user {GetFriendlyUserLabel(user)}",
                performedByUserId: _userManager.GetUserId(User),
                entityType: "User",
                entityId: user.Id
            );

            TempData["SuccessMessage"] = "Password reset successfully.";
            return RedirectToAction(nameof(ManageUsers));
        }

        // -------------------- SITIO --------------------
        public async Task<IActionResult> ManageSitios(string? search)
        {
            var q = _db.Sitios
                .Include(s => s.AssignedBhw)
                    .ThenInclude(u => u.Profile)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var low = search.Trim().ToLower();
                q = q.Where(s =>
                    s.Name.ToLower().Contains(low) ||
                    (s.AssignedBhw != null && (
                        ((s.AssignedBhw.Profile != null && (s.AssignedBhw.Profile.FirstName ?? "").ToLower().Contains(low)) ||
                         (s.AssignedBhw.Profile != null && (s.AssignedBhw.Profile.LastName ?? "").ToLower().Contains(low))) ||
                        ((s.AssignedBhw.DisplayName ?? s.AssignedBhw.UserName ?? "").ToLower().Contains(low))
                    ))
                );
            }

            var list = await q.OrderBy(s => s.Name).ToListAsync();
            ViewBag.CurrentSearch = search ?? "";
            return View(list);
        }

        [HttpGet]
        public async Task<IActionResult> CreateSitio()
        {
            await PopulateBhwDropdown(null);
            return View(new Sitio());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateSitio(Sitio model)
        {
            async Task Repopulate(string selectedId) => await PopulateBhwDropdown(selectedId);

            if (!ModelState.IsValid)
            {
                await Repopulate(model.AssignedBhwId ?? "");
                return View(model);
            }

            // Prevent duplicate sitio name
            if (await _db.Sitios.AnyAsync(s => s.Name == model.Name))
            {
                ModelState.AddModelError(nameof(model.Name), "A sitio with that name already exists.");
                await Repopulate(model.AssignedBhwId ?? "");
                return View(model);
            }

            // If an AssignedBhwId was selected, ensure that BHW is not already assigned to another sitio
            if (!string.IsNullOrWhiteSpace(model.AssignedBhwId))
            {
                var alreadyAssigned = await _db.Sitios.AnyAsync(s => !string.IsNullOrEmpty(s.AssignedBhwId) && s.AssignedBhwId == model.AssignedBhwId);
                if (alreadyAssigned)
                {
                    ModelState.AddModelError(nameof(model.AssignedBhwId), "Selected BHW is already assigned to another sitio. Choose a different BHW or unassign them first.");
                    await Repopulate(model.AssignedBhwId ?? "");
                    return View(model);
                }
            }

            model.AssignedBhwId = string.IsNullOrWhiteSpace(model.AssignedBhwId) ? null : model.AssignedBhwId;
            _db.Sitios.Add(model);
            await _db.SaveChangesAsync();

            await _auditService.AddAsync(
                action: "CreateSitio",
                details: $"Created sitio '{model.Name}'",
                performedByUserId: _userManager.GetUserId(User),
                entityType: "Sitio",
                entityId: model.Id.ToString()
            );

            TempData["SuccessMessage"] = "Sitio added.";
            return RedirectToAction(nameof(ManageSitios));
        }

        [HttpGet]
        public async Task<IActionResult> EditSitio(int id)
        {
            var sitio = await _db.Sitios.FindAsync(id);
            if (sitio == null) return NotFound();

            await PopulateBhwDropdown(sitio.AssignedBhwId);
            return View(sitio);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditSitio(int id, Sitio model)
        {
            if (id != model.Id) return BadRequest();

            if (!ModelState.IsValid)
            {
                await PopulateBhwDropdown(model.AssignedBhwId);
                return View(model);
            }

            var sitio = await _db.Sitios.FindAsync(id);
            if (sitio == null) return NotFound();

            // Prevent duplicate sitio name (excluding this sitio)
            if (await _db.Sitios.AnyAsync(s => s.Id != id && s.Name == model.Name))
            {
                ModelState.AddModelError(nameof(model.Name), "Another sitio has the same name.");
                await PopulateBhwDropdown(model.AssignedBhwId);
                return View(model);
            }

            // If an AssignedBhwId was selected, ensure that BHW is not already assigned to another sitio (exclude current sitio)
            if (!string.IsNullOrWhiteSpace(model.AssignedBhwId))
            {
                var alreadyAssigned = await _db.Sitios.AnyAsync(s => s.Id != id && !string.IsNullOrEmpty(s.AssignedBhwId) && s.AssignedBhwId == model.AssignedBhwId);
                if (alreadyAssigned)
                {
                    ModelState.AddModelError(nameof(model.AssignedBhwId), "Selected BHW is already assigned to another sitio. Choose a different BHW or unassign them first.");
                    await PopulateBhwDropdown(model.AssignedBhwId);
                    return View(model);
                }
            }

            sitio.Name = model.Name;
            sitio.Location = model.Location;
            sitio.AssignedBhwId = string.IsNullOrWhiteSpace(model.AssignedBhwId) ? null : model.AssignedBhwId;

            await _db.SaveChangesAsync();

            await _auditService.AddAsync(
                action: "EditSitio",
                details: $"Edited sitio '{sitio.Name}'",
                performedByUserId: _userManager.GetUserId(User),
                entityType: "Sitio",
                entityId: sitio.Id.ToString()
            );

            TempData["SuccessMessage"] = "Sitio updated.";
            return RedirectToAction(nameof(ManageSitios));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSitio(int? id)
        {
            if (!id.HasValue)
            {
                TempData["ErrorMessage"] = "Invalid sito id.";
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

            _db.Sitios.Remove(sitio);
            await _db.SaveChangesAsync();

            // Important: store the sitio NAME in the audit details so the UI can display it later
            await _auditService.AddAsync(
                action: "DeleteSitio",
                details: $"Deleted sitio '{sitioName}'",
                performedByUserId: _userManager.GetUserId(User),
                entityType: "Sitio",
                entityId: sitioId.ToString()
            );

            TempData["SuccessMessage"] = "Sitio deleted.";
            return RedirectToAction(nameof(ManageSitios));
        }

        // -------------------- AJAX SEARCH FOR USERS --------------------
        [HttpGet]
        public async Task<IActionResult> SearchUsers(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return Json(new object[0]);

            var term = q.Trim().ToLower();
            var numeric = int.TryParse(term, out var num);

            var query = _db.Users
                .Include(u => u.Profile)
                .Where(u => u.Email != SystemAdminEmail)
                .AsQueryable();

            query = query.Where(u =>
                (u.Email != null && u.Email.ToLower().Contains(term)) ||
                (u.DisplayName != null && u.DisplayName.ToLower().Contains(term)) ||
                (u.UserName != null && u.UserName.ToLower().Contains(term)) ||
                (u.Profile != null &&
                    (
                        (u.Profile.FirstName != null && u.Profile.FirstName.ToLower().Contains(term)) ||
                        (u.Profile.LastName != null && u.Profile.LastName.ToLower().Contains(term))
                    )
                ) ||
                (numeric && u.Profile != null && u.Profile.UserNumber == num) ||
                (u.Id != null && u.Id.ToLower().Contains(term))
            );

            var matches = await query
                .OrderBy(u => u.Profile != null && u.Profile.UserNumber != null ? u.Profile.UserNumber : int.MaxValue)
                .ThenBy(u => u.DisplayName ?? u.UserName)
                .Take(10)
                .Select(u => new
                {
                    id = u.Id,
                    userNumber = u.Profile != null ? u.Profile.UserNumber : (int?)null,
                    displayId = (u.Profile != null && u.Profile.UserNumber != null)
                                ? (u.Profile.UserNumber.ToString())
                                : (u.Id.Length >= 8 ? u.Id.Substring(0, 8) : u.Id),
                    name = (u.Profile != null ? ((u.Profile.FirstName ?? "") + " " + (u.Profile.LastName ?? "")).Trim() : (u.DisplayName ?? u.UserName)),
                    email = u.Email
                }).ToListAsync();

            return Json(matches);
        }

        // -------------------- PRIVATE HELPERS --------------------
        private async Task PopulateBhwDropdown(string? selectedId = null)
        {
            // Get role entry for BHW
            var bhwRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == "BHW" || r.NormalizedName == "BHW");
            var bhwUsersList = new List<ApplicationUser>();

            // Get list of AssignedBhwIds currently in use (exclude null/empty)
            var assignedIds = await _db.Sitios
                .Where(s => !string.IsNullOrEmpty(s.AssignedBhwId))
                .Select(s => s.AssignedBhwId!)
                .ToListAsync();

            if (bhwRole != null)
            {
                var bhwUserIds = await _db.UserRoles
                    .Where(ur => ur.RoleId == bhwRole.Id)
                    .Select(ur => ur.UserId)
                    .ToListAsync();

                if (bhwUserIds.Any())
                {
                    // Load users who are in BHW role
                    // Filter out users already assigned to a sitio, except keep the currently selected one (for edit)
                    bhwUsersList = await _db.Users
                        .Include(u => u.Profile)
                        .Where(u => bhwUserIds.Contains(u.Id)
                                    && (string.IsNullOrWhiteSpace(selectedId) ? !assignedIds.Contains(u.Id) : (!assignedIds.Contains(u.Id) || u.Id == selectedId)))
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
                    (u.Profile != null &&
                        (
                            (u.Profile.FirstName != null && u.Profile.FirstName.ToLower().Contains(low)) ||
                            (u.Profile.LastName != null && u.Profile.LastName.ToLower().Contains(low))
                        )
                    ) ||
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
                // include whole day if desired; here we treat toUtc as inclusive endpoint
                q = q.Where(u => u.CreatedAt != default && u.CreatedAt <= toUtc.Value);
            }

            if (!string.IsNullOrWhiteSpace(role))
            {
                // filter users by role via UserRoles join
                var roleEntity = _db.Roles.FirstOrDefault(r => r.Name == role || r.NormalizedName == role.ToUpper());
                if (roleEntity != null)
                {
                    var userIdsInRole = _db.UserRoles.Where(ur => ur.RoleId == roleEntity.Id).Select(ur => ur.UserId);
                    q = q.Where(u => userIdsInRole.Contains(u.Id));
                }
                else
                {
                    // role not found -> empty
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
        // GET: /Admin/Reports
        public async Task<IActionResult> Reports()
        {
            var vm = new ReportsViewModel();

            // Users counts
            vm.TotalUsers = await _db.Users.CountAsync();
            vm.InactiveUsers = await _db.Users.CountAsync(u => u.LockoutEnd.HasValue && u.LockoutEnd.Value > DateTimeOffset.UtcNow);
            vm.ActiveUsers = vm.TotalUsers - vm.InactiveUsers;

            // Sitios
            vm.TotalSitios = await _db.Sitios.CountAsync();

            // Recent activity (reuse MapAuditToFriendlyText)
            var recentAudit = await _db.AuditLogs.OrderByDescending(a => a.EventTime).Take(30).ToListAsync();

            var referencedUserIds = recentAudit
                .Where(a => string.Equals(a.EntityType, "User", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(a.EntityId))
                .Select(a => a.EntityId).Distinct().ToList();

            var userProfilesMap = new Dictionary<string, (int? UserNumber, string DisplayName)>();
            if (referencedUserIds.Any())
            {
                var users = await _db.Users.Include(u => u.Profile).Where(u => referencedUserIds.Contains(u.Id)).ToListAsync();
                foreach (var u in users)
                    userProfilesMap[u.Id] = (u.Profile?.UserNumber, u.Profile != null ? ((u.Profile.FirstName ?? "") + " " + (u.Profile.LastName ?? "")).Trim() : (u.DisplayName ?? u.UserName ?? ""));
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

            vm.RecentActivities = dedup.Select(a => new DashboardActivityVm
            {
                Timestamp = a.EventTime,
                Description = MapAuditToFriendlyText(a, userProfilesMap, sitioMap)
            }).OrderByDescending(x => x.Timestamp).ToList();

            // Users by month (last 6 months)
            var now = DateTime.UtcNow;
            var from = now.AddMonths(-5); // 6 months window
            var usersByMonth = await _db.Users
                .Where(u => u.CreatedAt != default && u.CreatedAt >= from)
                .ToListAsync();

            // NOTE: use u.CreatedAt (DateTime) — do not use .Value on a non-nullable DateTime
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

            // Sitios by assignment: assigned vs unassigned
            var totalSitios = await _db.Sitios.CountAsync();
            var assigned = await _db.Sitios.CountAsync(s => !string.IsNullOrWhiteSpace(s.AssignedBhwId));
            vm.SitiosByAssignment = new List<(string, int)> { ("Assigned", assigned), ("Unassigned", totalSitios - assigned) };

            return View(vm);
        }

        // GET: /Admin/ExportUsersCsv
        [HttpGet]
        public async Task<IActionResult> ExportReportsCsv(string? search, string? from, string? to, string? role = null, int? sitioId = null)
        {
            DateTime? fromUtc = null, toUtc = null;
            if (DateTime.TryParse(from, out var f)) fromUtc = f.ToUniversalTime();
            if (DateTime.TryParse(to, out var t)) toUtc = t.ToUniversalTime();

            var q = BuildFilteredUsersQuery(search, fromUtc, toUtc, role, sitioId);
            var users = await q.ToListAsync();

            // sitio map
            var sitioIds = users
                .Select(u => u.Profile?.SitioId)
                .Where(id => id.HasValue)
                .Select(id => id.Value)
                .Distinct()
                .ToList();

            var sitioMap = new Dictionary<int, Sitio>();
            if (sitioIds.Any())
            {
                sitioMap = await _db.Sitios.Where(s => sitioIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
            }

            // roles map
            var rolesMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in users)
            {
                if (string.IsNullOrWhiteSpace(u.Id)) continue;
                var roles = await _userManager.GetRolesAsync(u);
                rolesMap[u.Id] = roles != null ? string.Join(";", roles) : "";
            }

            var sb = new StringBuilder();

            // Users header
            sb.AppendLine("User ID,Email,Name,Roles,Joined,Sitio");

            for (int i = 0; i < users.Count; i++)
            {
                var u = users[i];
                var profile = u.Profile;

                // user id display: prefer profile.UserNumber else sequential number (1-based)
                var userIdDisplay = profile?.UserNumber.ToString() ?? (i + 1).ToString();
                var email = u.Email?.Replace("\"", "\"\"") ?? "";
                var fullName = profile != null
                    ? $"{(profile.FirstName ?? "").Trim()} {(profile.LastName ?? "").Trim()}".Trim()
                    : (u.DisplayName ?? u.UserName ?? "");

                var roles = "";
                if (!string.IsNullOrWhiteSpace(u.Id) && rolesMap.TryGetValue(u.Id, out var r)) roles = r ?? "";

                var joined = "";
                if (profile != null && profile.CreatedAt != default)
                    joined = profile.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");
                else if (u.CreatedAt != default)
                    joined = u.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");

                var sitioName = "";
                if (profile?.SitioId is int sid && sitioMap.TryGetValue(sid, out var sitio) && sitio != null)
                    sitioName = sitio.Name ?? "";

                // quote fields that may contain commas
                string Q(string s) => $"\"{(s ?? "").Replace("\"", "\"\"")}\"";

                sb.AppendLine(string.Join(",",
                    Q(userIdDisplay),
                    Q(email),
                    Q(fullName),
                    Q(roles),
                    Q(joined),
                    Q(sitioName)
                ));
            }

            // blank line then sitio section
            sb.AppendLine();
            sb.AppendLine("Sitio ID,Sitio Name,Assigned BHW");

            // list sitios sorted by name, but user wanted sequential 1,2,3 — create sequential
            var sitios = sitioMap.Values.OrderBy(s => s.Name ?? "").ToList();
            for (int i = 0; i < sitios.Count; i++)
            {
                var s = sitios[i];
                var sequential = (i + 1).ToString();
                var name = s.Name ?? "";
                var assigned = s.AssignedBhw != null
                    ? (!string.IsNullOrWhiteSpace(s.AssignedBhw.DisplayName) ? s.AssignedBhw.DisplayName : s.AssignedBhw.UserName ?? "")
                    : (s.AssignedBhwId ?? "");

                sb.AppendLine(string.Join(",",
                    $"\"{sequential}\"",
                    $"\"{name.Replace("\"", "\"\"")}\"",
                    $"\"{assigned.Replace("\"", "\"\"")}\""
                ));
            }

            // return bytes with BOM so Excel opens it as UTF-8
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return File(bytes, "text/csv", $"reports_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
        }



        // GET: /Admin/ExportUsersExcel
        [HttpGet]
        public async Task<IActionResult> ExportReportsExcel(string? search, string? from, string? to, string? role = null, int? sitioId = null)
        {
            DateTime? fromUtc = null, toUtc = null;
            if (DateTime.TryParse(from, out var f)) fromUtc = f.ToUniversalTime();
            if (DateTime.TryParse(to, out var t)) toUtc = t.ToUniversalTime();

            var q = BuildFilteredUsersQuery(search, fromUtc, toUtc, role, sitioId);
            var users = await q.ToListAsync();

            // sitio map
            var sitioIds = users
                .Select(u => u.Profile?.SitioId)
                .Where(id => id.HasValue)
                .Select(id => id.Value)
                .Distinct()
                .ToList();

            var sitioMap = new Dictionary<int, Sitio>();
            if (sitioIds.Any())
            {
                sitioMap = await _db.Sitios.Where(s => sitioIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
            }

            // roles map
            var rolesMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in users)
            {
                if (string.IsNullOrWhiteSpace(u.Id)) continue;
                var roles = await _userManager.GetRolesAsync(u);
                rolesMap[u.Id] = roles != null ? string.Join(";", roles) : "";
            }

            using var wb = new XLWorkbook();

            // Users sheet
            var wsUsers = wb.Worksheets.Add("Users");
            // headers
            wsUsers.Cell(1, 1).Value = "User ID";
            wsUsers.Cell(1, 2).Value = "Email";
            wsUsers.Cell(1, 3).Value = "Name";
            wsUsers.Cell(1, 4).Value = "Roles";
            wsUsers.Cell(1, 5).Value = "Joined";
            wsUsers.Cell(1, 6).Value = "Sitio";

            // rows
            var r = 2;
            for (int i = 0; i < users.Count; i++)
            {
                var u = users[i];
                var profile = u.Profile;

                var userIdDisplay = profile?.UserNumber.ToString() ?? (i + 1).ToString();
                var email = u.Email ?? "";
                var fullName = profile != null
                    ? $"{(profile.FirstName ?? "").Trim()} {(profile.LastName ?? "").Trim()}".Trim()
                    : (u.DisplayName ?? u.UserName ?? "");

                var rolesStr = "";
                if (!string.IsNullOrWhiteSpace(u.Id) && rolesMap.TryGetValue(u.Id, out var rstr)) rolesStr = rstr ?? "";

                var joined = "";
                if (profile != null && profile.CreatedAt != default)
                    joined = profile.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");
                else if (u.CreatedAt != default)
                    joined = u.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");

                var sitioName = "";
                if (profile?.SitioId is int sid && sitioMap.TryGetValue(sid, out var sitio) && sitio != null)
                    sitioName = sitio.Name ?? "";

                wsUsers.Cell(r, 1).Value = userIdDisplay;
                wsUsers.Cell(r, 2).Value = email;
                wsUsers.Cell(r, 3).Value = fullName;
                wsUsers.Cell(r, 4).Value = rolesStr;
                wsUsers.Cell(r, 5).Value = joined;
                wsUsers.Cell(r, 6).Value = sitioName;

                r++;
            }

            // style header
            wsUsers.Range(1, 1, 1, 6).Style.Font.SetBold(true);
            wsUsers.Columns().AdjustToContents();

            // Sitios sheet
            var wsSitios = wb.Worksheets.Add("Sitios");
            wsSitios.Cell(1, 1).Value = "Sitio ID";
            wsSitios.Cell(1, 2).Value = "Sitio Name";
            wsSitios.Cell(1, 3).Value = "Assigned BHW";

            var sitios = sitioMap.Values.OrderBy(s => s.Name ?? "").ToList();
            for (int i = 0; i < sitios.Count; i++)
            {
                var s = sitios[i];
                var sequential = (i + 1).ToString();
                var name = s.Name ?? "";
                var assigned = s.AssignedBhw != null
                    ? (!string.IsNullOrWhiteSpace(s.AssignedBhw.DisplayName) ? s.AssignedBhw.DisplayName : s.AssignedBhw.UserName ?? "")
                    : (s.AssignedBhwId ?? "");

                wsSitios.Cell(i + 2, 1).Value = sequential;
                wsSitios.Cell(i + 2, 2).Value = name;
                wsSitios.Cell(i + 2, 3).Value = assigned;
            }

            wsSitios.Range(1, 1, 1, 3).Style.Font.SetBold(true);
            wsSitios.Columns().AdjustToContents();

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            ms.Position = 0;
            var fileName = $"reports_{DateTime.UtcNow:yyyyMMdd_HHmmss}.xlsx";
            return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }





        [HttpGet]
        public async Task<IActionResult> ExportReportsPdf(string? search, string? from, string? to, string? role = null, int? sitioId = null)
        {
            DateTime? fromUtc = null, toUtc = null;
            if (DateTime.TryParse(from, out var f)) fromUtc = f.ToUniversalTime();
            if (DateTime.TryParse(to, out var t)) toUtc = t.ToUniversalTime();

            // Build filtered query (same helper as used elsewhere)
            var q = BuildFilteredUsersQuery(search, fromUtc, toUtc, role, sitioId);

            // Order the users the same way ManageUsers does so PDF matches UI order
            q = q.OrderBy(u => u.Email == SystemAdminEmail ? 0 : 1)
                 .ThenBy(u => u.Profile != null && u.Profile.UserNumber != null ? u.Profile.UserNumber : int.MaxValue)
                 .ThenBy(u => u.DisplayName ?? u.UserName);

            var users = await q.ToListAsync();

            // Get ALL sitios sorted by name (so sitio report matches Manage Sitios)
            var sitios = await _db.Sitios
                                  .Include(s => s.AssignedBhw)
                                      .ThenInclude(u => u.Profile)
                                  .OrderBy(s => s.Name)
                                  .ToListAsync();

            var sitioMap = sitios.ToDictionary(s => s.Id);

            // Build roles map (userId -> "Role1;Role2") so PdfReports can show Roles column
            var rolesMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in users)
            {
                if (string.IsNullOrWhiteSpace(u.Id)) continue;
                var roles = await _userManager.GetRolesAsync(u);
                rolesMap[u.Id] = roles != null ? string.Join(";", roles) : "";
            }

            // Pass ordered users, full sitioMap and rolesMap to PdfReports
            var document = new PdfReports(users, sitioMap, rolesMap);
            var pdfBytes = document.GeneratePdf();

            return File(pdfBytes, "application/pdf", $"reports_{DateTime.UtcNow:yyyyMMdd_HHmmss}.pdf");
        }





        // -------------------- SETTINGS (GET & POST) --------------------
        [HttpGet]
        public async Task<IActionResult> Settings()
        {
            var vm = await GetSettingsVmAsync();
            return View(vm);
        }

        // ---------- PATCHED Settings POST ----------
        [HttpPost]
        [ValidateAntiForgeryToken]
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
                        if (allErrors.Any())
                            TempData["ErrorMessage"] = "Validation error: " + string.Join(" | ", allErrors);

                        return View(vmErr);
                    }

                    // Resolve webroot (with safe fallback)
                    var webroot = _env.WebRootPath;
                    if (string.IsNullOrWhiteSpace(webroot) || !Directory.Exists(webroot))
                        webroot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                    var imagesFolder = Path.Combine(webroot, "images");
                    Directory.CreateDirectory(imagesFolder);

                    // helper to persist the path in both the key/value config and the systemconfigurations row (if present)
                    async Task PersistLogoPathAsync(string storedValue)
                    {
                        // save to key/value config (your existing mechanism)
                        await SetConfigValueAsync(Config_LogoPath, storedValue);

                        // ALSO write to systemconfigurations.LogoFileName (cover both storage patterns)
                        try
                        {
                            // attempt to find the likely config row (adjust predicate to your schema if needed)
                            var cfgRow = await _db.SystemConfigurations.FirstOrDefaultAsync(); // prefer explicit Id if you have one
                            if (cfgRow != null)
                            {
                                cfgRow.LogoFileName = storedValue;
                                cfgRow.ModifiedAt = DateTime.UtcNow;
                                await _db.SaveChangesAsync();
                            }
                        }
                        catch (Exception ex)
                        {
                            // non-fatal: log but continue (we already persisted via SetConfigValueAsync)
                            _logger.LogWarning(ex, "Failed to update SystemConfigurations.LogoFileName (non-fatal).");
                        }
                    }

                    // ---------- Handle file upload ----------
                    if (model.SiteLogoUpload != null && model.SiteLogoUpload.Length > 0)
                    {
                        try
                        {
                            // Always write as "logo.png" to keep usage consistent in the UI
                            var savedFileName = "logo.png";
                            var savePath = Path.Combine(imagesFolder, savedFileName);

                            // Write & overwrite atomically
                            using (var stream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                await model.SiteLogoUpload.CopyToAsync(stream);
                            }

                            // confirm file exists and is non-empty
                            if (!System.IO.File.Exists(savePath) || new FileInfo(savePath).Length == 0)
                            {
                                _logger.LogError("Logo file was written but does not exist or is empty at {path}", savePath);
                                TempData["ErrorMessage"] = "Upload completed but saved file is missing or empty.";
                                var vm = await GetSettingsVmAsync();
                                vm.SystemName = model.SystemName ?? vm.SystemName;
                                return View(vm);
                            }

                            // build stored value (leading slash + cache-buster)
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
                        // No file uploaded — accept a manual LogoPath (filename or URL)
                        if (!string.IsNullOrWhiteSpace(model.LogoPath))
                        {
                            var candidate = model.LogoPath.Trim();
                            bool looksLikeFilenameOnly = !candidate.Contains('/') && !candidate.Contains('\\') && !candidate.Contains("://");
                            if (looksLikeFilenameOnly)
                                candidate = "/images/" + candidate.TrimStart('/');
                            // Persist (both key/value and systemconfigurations)
                            await PersistLogoPathAsync(candidate);
                            _logger.LogInformation("Persisted manual LogoPath value: {val}", candidate);
                        }
                    }

                    // ---------- Save sanitized system name ----------
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

                    // Audit + success
                    var adminId = _userManager.GetUserId(User);
                    await _auditService.AddAsync(
                        action: "EditSettings",
                        details: $"Updated system settings: Name='{cleanedName}'",
                        performedByUserId: adminId,
                        entityType: "System",
                        entityId: adminId ?? ""
                    );

                    TempData["SuccessMessage"] = "Settings updated.";
                    return RedirectToAction(nameof(Settings));
                }

                // ---------- CHANGE PASSWORD flow (unchanged) ----------
                if (suppliedAction == "changepassword")
                {
                    // your existing change-password logic...
                    if (string.IsNullOrWhiteSpace(model.ResetNewPassword) || string.IsNullOrWhiteSpace(model.ResetNewPasswordConfirm) ||
                        model.ResetNewPassword != model.ResetNewPasswordConfirm)
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
                        entityId: targetUser.Id
                    );

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

        // Also update GetSettingsVmAsync to fallback to systemconfigurations table if key/value is empty
        private async Task<SettingsViewModel> GetSettingsVmAsync()
        {
            var vm = new SettingsViewModel
            {
                SystemName = await GetConfigValueAsync(Config_SystemName) ?? "Barangay System",
                LogoPath = await GetConfigValueAsync(Config_LogoPath) ?? "/images/logo.png",
            };

            // Fallback: if LogoPath is still default, try reading the systemconfigurations row's LogoFileName
            if (string.IsNullOrWhiteSpace(vm.LogoPath) || vm.LogoPath == "/images/logo.png")
            {
                try
                {
                    var cfgRow = await _db.SystemConfigurations.FirstOrDefaultAsync(sc => sc.SiteName == "LogoPath");
                    // adjust if you need Id filter
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
            foreach (var c in candidates)
                if (t.GetProperty(c, BindingFlags.Public | BindingFlags.Instance) != null)
                    return c;
            var fallback = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                            .FirstOrDefault(p => p.PropertyType == typeof(string));
            return fallback?.Name ?? "Key";
        }

        private static string DetectConfigValuePropertyName()
        {
            var t = typeof(SystemConfiguration);
            var candidates = new[] { "Value", "ConfigValue", "SettingValue", "Val", "Setting", "LogoFileName", "LogoPath", "SiteName" };
            foreach (var c in candidates)
                if (t.GetProperty(c, BindingFlags.Public | BindingFlags.Instance) != null)
                    return c;
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
                    if (action.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0)
                        return $"➕ New User Added: {userLabel}";
                    if (action.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0)
                        return $"🗑️ Deleted user {userLabel}";
                    if (action.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0)
                        return $"✏️ Edited user {userLabel}";
                    // check Deactivate before Activate because "Deactivate" contains "Activate"
                    if (action.IndexOf("Deactivate", StringComparison.OrdinalIgnoreCase) >= 0)
                        return $"🔒 Deactivated user {userLabel}";
                    if (action.IndexOf("Activate", StringComparison.OrdinalIgnoreCase) >= 0)
                        return $"🔓 Activated user {userLabel}";

                    return $"{action}: {userLabel}{(string.IsNullOrWhiteSpace(details) ? "" : " - " + details)}";
                }
                else
                {
                    var extracted = ExtractLabelFromDetails(details);
                    if (!string.IsNullOrWhiteSpace(extracted))
                    {
                        if (action.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0)
                            return $"➕ New User Added: {extracted}";
                        if (action.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0)
                            return $"🗑️ Deleted user {extracted}";
                        if (action.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0)
                            return $"✏️ Edited user {extracted}";
                        // check Deactivate before Activate
                        if (action.IndexOf("Deactivate", StringComparison.OrdinalIgnoreCase) >= 0)
                            return $"🔒 Deactivated user {extracted}";
                        if (action.IndexOf("Activate", StringComparison.OrdinalIgnoreCase) >= 0)
                            return $"🔓 Activated user {extracted}";

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
                    if (action.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0)
                        return $"➕ Created sitio {label}";
                    if (action.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0)
                        return $"🗑️ Deleted sitio {label}";
                    if (action.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0)
                        return $"✏️ Edited sitio {label}";

                    return $"{action}: {label}{(string.IsNullOrWhiteSpace(details) ? "" : " - " + details)}";
                }

                // Try to extract name from details if present
                var nameMatch = Regex.Match(details, @"(?:Created|Deleted|Edited)\s+sitio\s*'([^']+)'", RegexOptions.IgnoreCase);
                if (nameMatch.Success)
                {
                    var nm = nameMatch.Groups[1].Value;
                    if (action.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0)
                        return $"🗑️ Deleted sitio '{nm}'";
                    if (action.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0)
                        return $"➕ Created sitio '{nm}'";
                    if (action.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0)
                        return $"✏️ Edited sito '{nm}'";

                    return $"{action}: '{nm}'{(string.IsNullOrWhiteSpace(details) ? "" : " - " + details)}";
                }

                if (int.TryParse(a.EntityId, out var sid))
                {
                    if (action.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0)
                        return $"🗑️ Deleted sitio (Id: {sid})";
                    if (action.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0)
                        return $"➕ Created sitio (Id: {sid})";

                    return $"{action}: Sitio (Id: {sid}){(string.IsNullOrWhiteSpace(details) ? "" : " - " + details)}";
                }

                var shortId2 = a.EntityId.Length >= 8 ? a.EntityId.Substring(0, 8) : a.EntityId;
                return $"{action}: {shortId2}{(string.IsNullOrWhiteSpace(details) ? "" : " - " + details)}";
            }

            // Generic fallbacks - make sure Deactivate is checked before Activate
            if (action.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0)
                return $"🗑️ {details}";
            if (action.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0)
                return $"➕ {details}";
            if (action.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0)
                return $"✏️ {details}";
            if (action.IndexOf("Deactivate", StringComparison.OrdinalIgnoreCase) >= 0)
                return $"🔒 {details}";
            if (action.IndexOf("Activate", StringComparison.OrdinalIgnoreCase) >= 0)
                return $"🔓 {details}";

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

        // -------------------- Prompt page that tells admin to confirm their email --------------------
        [HttpGet]
        public IActionResult PromptVerifyEmail()
        {
            // This view should show the admin that their account isn't verified and include
            // a form/button that posts to SendVerificationEmail to resend.
            return View(); // create Views/Admin/PromptVerifyEmail.cshtml
        }

        // -------------------- Send verification email (POST) --------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
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

        // -------------------- Confirm email endpoint (public) --------------------
        // The verification link will point here. It must be AllowAnonymous so email recipients can click.
        [HttpGet]
        [AllowAnonymous]
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
                // DO NOT manually WebUtility.UrlDecode here. The framework already decodes querystring for you.
                var result = await _userManager.ConfirmEmailAsync(user, token);
                if (result.Succeeded)
                {
                    await _auditService.AddAsync(
                        action: "ConfirmEmail",
                        details: $"Email confirmed for user {GetFriendlyUserLabel(user)}",
                        performedByUserId: user.Id,
                        entityType: "User",
                        entityId: user.Id
                    );

                    TempData["SuccessMessage"] = "Email address confirmed. You may now use admin features.";
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


        // -------------------- Helper to generate token + send email --------------------
        private async Task SendConfirmationEmailAsync(ApplicationUser user)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));
            if (string.IsNullOrWhiteSpace(user.Email)) throw new InvalidOperationException("User has no email.");

            // generate token (raw)
            var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);

            // Build confirmation link - pass raw token to Url.Action (framework will encode it)
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
                entityId: user.Id
            );
        }

    }
}
