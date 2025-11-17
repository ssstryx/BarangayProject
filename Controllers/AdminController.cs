using BarangayProject.Data;
using BarangayProject.Models;
using BarangayProject.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Net; // <- for HtmlDecode

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

        // seeded admin we hide from lists
        private const string SystemAdminEmail = "admin@gmail.com";

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
            IWebHostEnvironment env)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _role_manager_check(roleManager);
            _roleManager = roleManager ?? throw new ArgumentNullException(nameof(roleManager));
            _auditService = auditService ?? throw new ArgumentNullException(nameof(auditService));
            _env = env ?? throw new ArgumentNullException(nameof(env));
        }

        // small helper to avoid analyzer warning (no-op)
        private void _role_manager_check(RoleManager<IdentityRole>? dummy) { /* no-op */ }

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

            q = q.Where(u => u.Email != SystemAdminEmail);

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

            var usersQuery = q
                .OrderBy(u => u.Profile != null && u.Profile.UserNumber != null ? u.Profile.UserNumber : int.MaxValue)
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

        // -------------------- ADD USER --------------------
        [HttpGet]
        public IActionResult AddUser()
        {
            ViewData["AvailableRoles"] = new[] { "Admin", "BNS", "BHW" };
            return View(new AddUserVm());
        }

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
                EmailConfirmed = true,
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

            TempData["SuccessMessage"] = "User added successfully.";
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

        // -------------------- DELETE USER --------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteUser(string id)
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

            var adminId = _userManager.GetUserId(User);

            var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == id);
            if (profile != null)
            {
                _db.UserProfiles.Remove(profile);
                await _db.SaveChangesAsync();
            }

            var result = await _userManager.DeleteAsync(user);
            if (!result.Succeeded)
            {
                TempData["ErrorMessage"] = "Failed to delete user.";
                return RedirectToAction(nameof(ManageUsers));
            }

            await _auditService.AddAsync(
                action: "DeleteUser",
                details: $"Deleted user {GetFriendlyUserLabel(user)}",
                performedByUserId: adminId,
                entityType: "User",
                entityId: user.Id
            );

            TempData["SuccessMessage"] = "User deleted successfully.";
            return RedirectToAction(nameof(ManageUsers));
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
                .Select(u => new {
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
            // If ModelState invalid, surface errors and return populated VM
            if (!ModelState.IsValid)
            {
                var allErrors = ModelState.Values
                                .SelectMany(v => v.Errors)
                                .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? (e.Exception?.Message ?? "Unknown error") : e.ErrorMessage)
                                .ToList();

                var vm = await GetSettingsVmAsync();
                vm.SystemName = model.SystemName ?? vm.SystemName;
                vm.LogoPath = model.LogoPath ?? vm.LogoPath;

                if (allErrors.Any())
                {
                    TempData["ErrorMessage"] = "Validation error: " + string.Join(" | ", allErrors);
                }

                return View(vm);
            }

            // -------------------------
            // Password reset validation
            // -------------------------
            bool passwordFieldsSupplied = !string.IsNullOrWhiteSpace(model.ResetNewPassword) || !string.IsNullOrWhiteSpace(model.ResetNewPasswordConfirm);

            if (passwordFieldsSupplied)
            {
                // confirm match
                if (model.ResetNewPassword != model.ResetNewPasswordConfirm)
                {
                    ModelState.AddModelError(nameof(model.ResetNewPasswordConfirm), "New password and confirmation do not match.");
                }

                // basic strength check: min 8, at least one digit and one symbol
                var pass = model.ResetNewPassword ?? "";
                // <-- FIX: treat underscore '_' as acceptable symbol too
                var meetsBasic = pass.Length >= 8 && Regex.IsMatch(pass, @"\d") && Regex.IsMatch(pass, @"[^\w\s]|_");
                if (!meetsBasic)
                {
                    ModelState.AddModelError(nameof(model.ResetNewPassword), "Password must be at least 8 characters and include at least one number and one symbol.");
                }

                // If modelstate now has errors, return view
                if (!ModelState.IsValid)
                {
                    var vm = await GetSettingsVmAsync();
                    vm.SystemName = model.SystemName ?? vm.SystemName;
                    vm.LogoPath = model.LogoPath ?? vm.LogoPath;

                    var allErrors = ModelState.Values.SelectMany(v => v.Errors)
                        .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? (e.Exception?.Message ?? "Unknown error") : e.ErrorMessage)
                        .ToList();

                    TempData["ErrorMessage"] = "Validation error: " + string.Join(" | ", allErrors);
                    return View(vm);
                }
            }

            string? savedLogoValueForConfig = null;

            // Use model.SiteLogoUpload (view uses asp-for="SiteLogoUpload")
            var uploadedFile = model.SiteLogoUpload;
            if (uploadedFile != null && uploadedFile.Length > 0)
            {
                var imagesFolder = Path.Combine(_env.WebRootPath ?? "wwwroot", "images");
                if (!Directory.Exists(imagesFolder)) Directory.CreateDirectory(imagesFolder);

                var ext = Path.GetExtension(uploadedFile.FileName);
                var fileName = "logo" + (string.IsNullOrEmpty(ext) ? ".png" : ext);
                var savePath = Path.Combine(imagesFolder, fileName);

                using (var stream = new FileStream(savePath, FileMode.Create))
                {
                    await uploadedFile.CopyToAsync(stream);
                }

                // store path + cache buster so browser will pick up new image
                savedLogoValueForConfig = "/images/" + fileName + "?v=" + DateTime.UtcNow.Ticks.ToString();
                await SetConfigValueAsync(Config_LogoPath, savedLogoValueForConfig);
            }
            else
            {
                // No upload: if model provides a custom LogoPath (external URL or non-wwwroot path), save as-is
                if (!string.IsNullOrWhiteSpace(model.LogoPath))
                {
                    var candidate = model.LogoPath.Trim();

                    // If user provided a plain filename, assume images folder
                    bool looksLikeFilenameOnly = !candidate.Contains('/') && !candidate.Contains('\\') && !candidate.Contains("://");
                    if (looksLikeFilenameOnly)
                        candidate = "/images/" + candidate.TrimStart('/');

                    savedLogoValueForConfig = candidate;
                    await SetConfigValueAsync(Config_LogoPath, savedLogoValueForConfig);
                }
            }

            // sanitize system name: strip <br>, other tags, decode entities, collapse whitespace
            string CleanSystemName(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return "";
                // replace common <br> variants with space
                var tmp = raw.Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
                             .Replace("<br/>", " ", StringComparison.OrdinalIgnoreCase)
                             .Replace("<br />", " ", StringComparison.OrdinalIgnoreCase);

                // remove other tags
                tmp = Regex.Replace(tmp, "<.*?>", " ");
                // decode html entities
                tmp = WebUtility.HtmlDecode(tmp);
                // collapse whitespace
                tmp = Regex.Replace(tmp, @"\s+", " ").Trim();
                return tmp;
            }

            var cleanedSystemName = CleanSystemName(model.SystemName ?? "");

            // persist settings (SidebarCompact stored as "1"/"0" - keep key for compatibility)
            await SetConfigValueAsync(Config_SystemName, cleanedSystemName);

            // -------------------------
            // Reset password (if requested)
            // -------------------------
            if (passwordFieldsSupplied)
            {
                var targetUser = await _userManager.FindByIdAsync(model.ResetUserId);
                if (targetUser == null)
                {
                    ModelState.AddModelError(nameof(model.ResetUserId), "Selected user not found.");
                    var vm = await GetSettingsVmAsync();
                    vm.SystemName = model.SystemName ?? vm.SystemName;
                    vm.LogoPath = model.LogoPath ?? vm.LogoPath;
                    TempData["ErrorMessage"] = "Selected user not found.";
                    return View(vm);
                }

                // Run configured Identity password validators in addition to our basic checks
                foreach (var validator in _userManager.PasswordValidators)
                {
                    var validationResult = await validator.ValidateAsync(_userManager, targetUser, model.ResetNewPassword);
                    if (!validationResult.Succeeded)
                    {
                        foreach (var e in validationResult.Errors)
                        {
                            ModelState.AddModelError(nameof(model.ResetNewPassword), e.Description);
                        }
                        var vm = await GetSettingsVmAsync();
                        vm.SystemName = model.SystemName ?? vm.SystemName;
                        vm.LogoPath = model.LogoPath ?? vm.LogoPath;

                        var allErrors = ModelState.Values.SelectMany(v => v.Errors)
                            .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? (e.Exception?.Message ?? "Unknown error") : e.ErrorMessage)
                            .ToList();

                        TempData["ErrorMessage"] = "Password validation failed: " + string.Join(" | ", allErrors);
                        return View(vm);
                    }
                }

                // perform reset
                var token = await _userManager.GeneratePasswordResetTokenAsync(targetUser);
                var resetRes = await _userManager.ResetPasswordAsync(targetUser, token, model.ResetNewPassword);
                if (!resetRes.Succeeded)
                {
                    foreach (var e in resetRes.Errors) ModelState.AddModelError("", e.Description);
                    var vm = await GetSettingsVmAsync();
                    vm.SystemName = model.SystemName ?? vm.SystemName;
                    vm.LogoPath = model.LogoPath ?? vm.LogoPath;

                    var allErrors = ModelState.Values.SelectMany(v => v.Errors)
                        .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? (e.Exception?.Message ?? "Unknown error") : e.ErrorMessage)
                        .ToList();

                    TempData["ErrorMessage"] = "Password reset failed: " + string.Join(" | ", allErrors);
                    return View(vm);
                }

                await _auditService.AddAsync(
                    action: "ResetPassword",
                    details: $"Reset password for user {GetFriendlyUserLabel(targetUser)}",
                    performedByUserId: _userManager.GetUserId(User),
                    entityType: "User",
                    entityId: targetUser.Id
                );

                TempData["SuccessMessage"] = "Settings updated. Password reset successful.";
                return RedirectToAction(nameof(Settings));
            }

            var adminId = _userManager.GetUserId(User);
            await _auditService.AddAsync(
                action: "EditSettings",
                details: $"Updated system settings: Name='{cleanedSystemName}'",
                performedByUserId: adminId,
                entityType: "System",
                entityId: adminId ?? ""
            );

            TempData["SuccessMessage"] = "Settings updated.";
            return RedirectToAction(nameof(Settings));
        }

        private async Task<SettingsViewModel> GetSettingsVmAsync()
        {
            var vm = new SettingsViewModel
            {
                SystemName = await GetConfigValueAsync(Config_SystemName) ?? "Barangay System",
                LogoPath = await GetConfigValueAsync(Config_LogoPath) ?? "/images/logo.png",
            };

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
                        return $"✏️ Edited sitio '{nm}'";

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
    }
}
