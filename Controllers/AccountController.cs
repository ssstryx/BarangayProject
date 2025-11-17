using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using BarangayProject.Models;
using BarangayProject.Data;

namespace BarangayProject.Controllers
{
    public class AccountController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly ApplicationDbContext _db;

        public AccountController(UserManager<ApplicationUser> userManager,
                                 SignInManager<ApplicationUser> signInManager,
                                 RoleManager<IdentityRole> roleManager,
                                 ApplicationDbContext db)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _roleManager = roleManager;
            _db = db;
        }

        // ---- LOGIN ----
        [HttpGet]
        public IActionResult Login(string returnUrl = null)
        {
            return View(new LoginViewModel { ReturnUrl = returnUrl });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            // Try find the user by email first
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
            {
                // Do not reveal whether the email exists. Add error to Password field so it displays under the password input.
                ModelState.AddModelError(nameof(model.Password), "Incorrect email or password.");
                return View(model);
            }

            // Attempt sign-in
            var result = await _signInManager.PasswordSignInAsync(user.UserName, model.Password, model.RememberMe, lockoutOnFailure: true);
            if (result.Succeeded)
            {
                if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
                    return Redirect(model.ReturnUrl);

                var roles = await _userManager.GetRolesAsync(user);

                // Case-insensitive checks
                if (roles.Any(r => r.Equals("Admin", StringComparison.OrdinalIgnoreCase)))
                    return RedirectToAction("Index", "Admin"); // AdminController.Index

                if (roles.Any(r => r.Equals("BNS", StringComparison.OrdinalIgnoreCase)))
                    return RedirectToAction("Index", "Bns");

                if (roles.Any(r => r.Equals("BHW", StringComparison.OrdinalIgnoreCase)))
                    return RedirectToAction("Index", "Bhw");

                return RedirectToAction("Index", "Home");
            }


            if (result.IsLockedOut)
            {
                // Optionally show lockout view
                return View("Lockout");
            }

            // For all other failures, set an inline error on the Password field
            ModelState.AddModelError(nameof(model.Password), "Incorrect email or password.");
            return View(model);
        }



        // ---- LOGOUT ----
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();
            TempData["InfoMessage"] = "You have been logged out.";
            return RedirectToAction("Login", "Account");
        }

        // ---- FORGOT PASSWORD (replaces Register) ----
        // Show the forgot password form
        [HttpGet]
        public IActionResult ForgotPassword()
        {
            return View(new ForgotPasswordViewModel());
        }

        // Process forgot password submission
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            var user = await _userManager.FindByEmailAsync(model.Email);
            // For security do not reveal that the email does not exist. Still generate a "successful" message.
            if (user == null || !(await _userManager.IsEmailConfirmedAsync(user)))
            {
                // Show same message whether or not the user exists or email confirmed.
                TempData["InfoMessage"] = "If that email exists in our system, a password reset link has been generated and/or emailed.";
                return RedirectToAction(nameof(Login));
            }

            // generate reset token
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var callbackUrl = Url.Action("ResetPassword", "Account",
                new { token = Uri.EscapeDataString(token), email = user.Email }, Request.Scheme);

            // If you have an IEmailSender configured, send email here.
            // Otherwise (development) expose the link via TempData so you can click it.
            // NOTE: In production remove the TempData link and send email.
            TempData["InfoMessage"] = "A password reset link was generated. If email sending is configured you'll receive it by email.";
            TempData["ResetLink"] = callbackUrl;

            // Example: if you have an email sender service, call it:
            // await _emailSender.SendEmailAsync(user.Email, "Reset Password",
            //    $"Please reset your password by <a href='{HtmlEncoder.Default.Encode(callbackUrl)}'>clicking here</a>.");

            return RedirectToAction(nameof(Login));
        }

        // ---- RESET PASSWORD (user arrives here from email or TempData link) ----
        [HttpGet]
        public IActionResult ResetPassword(string token = null, string email = null)
        {
            if (token == null || email == null)
            {
                // Token or email missing - show friendly view or redirect
                TempData["ErrorMessage"] = "Invalid password reset token.";
                return RedirectToAction(nameof(Login));
            }

            var vm = new ResetPasswordViewModel
            {
                Token = token,
                Email = email
            };
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
            {
                // don't reveal user doesn't exist
                TempData["InfoMessage"] = "Password reset completed (if the account existed).";
                return RedirectToAction(nameof(Login));
            }

            var decodedToken = model.Token;
            try
            {
                var result = await _userManager.ResetPasswordAsync(user, decodedToken, model.Password);
                if (result.Succeeded)
                {
                    TempData["SuccessMessage"] = "Your password has been reset. Please login.";
                    return RedirectToAction(nameof(Login));
                }

                foreach (var err in result.Errors)
                    ModelState.AddModelError(string.Empty, err.Description);

                return View(model);
            }
            catch
            {
                ModelState.AddModelError(string.Empty, "Invalid token or an error occurred. Request a new password reset.");
                return View(model);
            }
        }

        // ---- OTHER PAGES ----
        [HttpGet]
        public IActionResult AccessDenied() => View();

        [HttpGet]
        public IActionResult Lockout() => View();
    }
}
