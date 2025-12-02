using System;
using System.IO;
using BarangayProject.Data;
using BarangayProject.Models.AdminModel;
using BarangayProject.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Infrastructure;


// allow community usage
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

// Bind EmailSettings from configuration (single call)
builder.Services.Configure<BarangayProject.Services.EmailSettings>(builder.Configuration.GetSection("EmailSettings"));

// Register your SMTP email sender (fully-qualified interface to avoid ambiguity)
builder.Services.AddTransient<BarangayProject.Services.IEmailSender, BarangayProject.Services.SmtpEmailSender>();

// Add services
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

// Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 6;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
})
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

// Configure cookies
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
});

// Other services
builder.Services.AddScoped<AuditService>();
builder.Services.AddHostedService<BarangayProject.Services.AuditCleanupService>();

builder.Services.AddControllersWithViews();

var app = builder.Build();

// Seed roles and admin user (wrapped with logging + safe catch)
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetService<ILogger<Program>>();
    try
    {
        logger?.LogInformation("Starting seed data...");
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var db = services.GetRequiredService<ApplicationDbContext>();

        await SeedData.InitializeAsync(userManager, roleManager, db);

        logger?.LogInformation("Seed data completed.");
    }
    catch (Exception ex)
    {
        logger?.LogError(ex, "An error occurred while seeding the database.");
    }
}

// Configure middleware
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();


app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}");

app.Run();
