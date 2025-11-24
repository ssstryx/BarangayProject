using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;
using BarangayProject.Models;

namespace BarangayProject.Data
{
    public static class SeedData
    {
        private static readonly string[] Roles = new[] { "Admin", "BNS", "BHW" };
        private const string AdminEmail = "barangayproject.mailer@gmail.com";
        private const string AdminPassword = "Admin123"; // change before production

        public static async Task InitializeAsync(UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager, ApplicationDbContext db)
        {
            // 1) Ensure roles
            foreach (var role in Roles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    await roleManager.CreateAsync(new IdentityRole(role));
                }
            }

            // 2) Create admin user if missing, set IsAdmin true and add to role
            var adminUser = await userManager.FindByEmailAsync(AdminEmail);
            if (adminUser == null)
            {
                adminUser = new ApplicationUser
                {
                    UserName = AdminEmail,
                    Email = AdminEmail,
                    EmailConfirmed = true,
                    DisplayName = "System Administrator",
                    IsAdmin = true
                };

                var result = await userManager.CreateAsync(adminUser, AdminPassword);
                if (result.Succeeded)
                {
                    await userManager.AddToRoleAsync(adminUser, "Admin");
                }
                else
                {
                    // optional: throw or log errors in your environment
                    throw new Exception("Failed to create seed admin user: " + string.Join("; ", result.Errors.Select(e => e.Description)));
                }
            }
            else
            {
                // ensure flag is set and role membership present
                if (!adminUser.IsAdmin)
                {
                    adminUser.IsAdmin = true;
                    await userManager.UpdateAsync(adminUser);
                }

                if (!await userManager.IsInRoleAsync(adminUser, "Admin"))
                {
                    await userManager.AddToRoleAsync(adminUser, "Admin");
                }
            }

            // 3) Backfill existing users who are in Admin role (in case IsAdmin property was added later)
            try
            {
                var adminRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "Admin");
                if (adminRole != null)
                {
                    var adminUserIds = await db.UserRoles
                        .Where(ur => ur.RoleId == adminRole.Id)
                        .Select(ur => ur.UserId)
                        .Distinct()
                        .ToListAsync();

                    foreach (var userId in adminUserIds)
                    {
                        var u = await userManager.FindByIdAsync(userId);
                        if (u != null && !u.IsAdmin)
                        {
                            u.IsAdmin = true;
                            await userManager.UpdateAsync(u);
                        }
                    }
                }
            }
            catch
            {
             
            }
        }
    }
}
