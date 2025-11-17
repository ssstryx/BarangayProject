using Microsoft.AspNetCore.Identity;
using System.Threading.Tasks;
using BarangayProject.Models;

namespace BarangayProject.Data
{
    public static class SeedData
    {
        private static readonly string[] Roles = new[] { "Admin", "BNS", "BHW" };
        private const string AdminEmail = "admin@gmail.com";
        private const string AdminPassword = "Admin123"; // change before production

        public static async Task InitializeAsync(UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
        {
            foreach (var role in Roles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    await roleManager.CreateAsync(new IdentityRole(role));
                }
            }

            var adminUser = await userManager.FindByEmailAsync(AdminEmail);
            if (adminUser == null)
            {
                adminUser = new ApplicationUser
                {
                    UserName = AdminEmail,
                    Email = AdminEmail,
                    EmailConfirmed = true,
                    DisplayName = "System Administrator"
                };

                var result = await userManager.CreateAsync(adminUser, AdminPassword);
                if (result.Succeeded)
                {
                    await userManager.AddToRoleAsync(adminUser, "Admin");
                }
            }
        }
    }
}
