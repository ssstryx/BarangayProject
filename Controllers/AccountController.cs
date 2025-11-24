using System;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using BarangayProject.Data;
using BarangayProject.Models;
using BarangayProject.Services;

namespace BarangayProject.Controllers
{
    public class AccountController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly ApplicationDbContext _db;
        private readonly ILogger<AccountController> _logger;
        private readonly IEmailSender _emailSender;

        public AccountController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            RoleManager<IdentityRole> roleManager,
            ApplicationDbContext db,
            ILogger<AccountController> logger,
            IEmailSender emailSender)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _roleManager = roleManager;
            _db = db;
            _logger = logger;
            _emailSender = emailSender;
        }

        // -------------------------------
        // Token helpers
        // -------------------------------
        private static string EncodeToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return token;
            var bytes = Encoding.UTF8.GetBytes(token);
            return WebEncoders.Base64UrlEncode(bytes);
        }

        private static string DecodeToken(string encodedToken)
        {
            if (string.IsNullOrEmpty(encodedToken)) return null;
            try
            {
                var bytes = WebEncoders.Base64UrlDecode(encodedToken);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }

        // -------------------------------
        // LOGIN
        // -------------------------------
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

            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
            {
                ModelState.AddModelError(nameof(model.Password), "Incorrect email or password.");
                return View(model);
            }

            var result = await _signInManager.PasswordSignInAsync(
                user.UserName,
                model.Password,
                model.RememberMe,
                lockoutOnFailure: true
            );

            if (result.Succeeded)
            {
                TempData.Remove("InfoMessage");
                TempData.Remove("LogoutMessage");
                TempData.Remove("ErrorMessage");
                TempData.Remove("SuccessMessage");

                if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
                    return LocalRedirect(model.ReturnUrl);

                var roles = await _userManager.GetRolesAsync(user);

                if (roles.Any(r => r.Equals("Admin", StringComparison.OrdinalIgnoreCase)))
                    return RedirectToAction("Index", "Admin");

                if (roles.Any(r => r.Equals("BNS", StringComparison.OrdinalIgnoreCase)))
                    return RedirectToAction("Index", "Bns");

                if (roles.Any(r => r.Equals("BHW", StringComparison.OrdinalIgnoreCase)))
                    return RedirectToAction("Index", "Bhw");

                return RedirectToAction("Index", "Home");
            }

            if (result.IsLockedOut)
                return View("Lockout");

            if (result.IsNotAllowed)
            {
                ModelState.AddModelError("", "You are not allowed to sign in yet (email not confirmed?).");
                return View(model);
            }

            ModelState.AddModelError(nameof(model.Password), "Incorrect email or password.");
            return View(model);
        }

        // -------------------------------
        // LOGOUT
        // -------------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();
            TempData["LogoutMessage"] = "You have been logged out.";
            return RedirectToAction("Login", "Account");
        }

        // -------------------------------
        // FORGOT PASSWORD (GET)
        // -------------------------------
        [HttpGet]
        public IActionResult ForgotPassword()
        {
            return View(new ForgotPasswordViewModel());
        }

        // -------------------------------
        // FORGOT PASSWORD (POST) - sends email
        // -------------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            _logger.LogInformation("ForgotPassword called for {Email}", model.Email);

            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null || !(await _userManager.IsEmailConfirmedAsync(user)))
            {
                // Generic response to avoid user enumeration
                TempData["InfoMessage"] = "If that email exists, a reset link has been generated.";
                return RedirectToAction(nameof(Login));
            }

            // generate token & url
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var encodedToken = EncodeToken(token);

            var callbackUrl = Url.Action(nameof(ResetPassword), "Account",
                new { token = encodedToken, email = user.Email },
                Request.Scheme);

#if DEBUG
            TempData["ResetLink"] = callbackUrl;
#endif

            // send email
            try
            {
                var encodedCallback = HtmlEncoder.Default.Encode(callbackUrl ?? "");
                var htmlMessage = $@"
                    <p>You requested a password reset for your account.</p>
                    <p>Please <a href=""{encodedCallback}"">click here to reset your password</a>.</p>
                    <p>If you did not request this, you may ignore this email.</p>";

                await _emailSender.SendEmailAsync(user.Email, "Reset your password", htmlMessage);

                _logger.LogInformation("Password reset email sent to {Email}", user.Email);
                TempData["InfoMessage"] = "A password reset link has been sent to your email.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending password reset email to {Email}", user.Email);
#if DEBUG
                // show debug link if available
                TempData["InfoMessage"] = "A password reset link was generated (email send failed in DEBUG).";
#else
                TempData["InfoMessage"] = "If that email exists, a reset link has been generated.";
#endif
            }

            return RedirectToAction(nameof(Login));
        }

        // -------------------------------
        // RESET PASSWORD (GET)
        // -------------------------------
        [HttpGet]
        public IActionResult ResetPassword(string token = null, string email = null)
        {
            if (token == null || email == null)
            {
                TempData["ErrorMessage"] = "Invalid password reset token.";
                return RedirectToAction(nameof(Login));
            }

            return View(new ResetPasswordViewModel
            {
                Token = token,
                Email = email
            });
        }

        // -------------------------------
        // RESET PASSWORD (POST)
        // -------------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
            {
                TempData["InfoMessage"] = "Password reset completed (if account existed).";
                return RedirectToAction(nameof(Login));
            }

            var decodedToken = DecodeToken(model.Token);
            if (decodedToken == null)
            {
                ModelState.AddModelError("", "Invalid or expired token.");
                return View(model);
            }

            var result = await _userManager.ResetPasswordAsync(user, decodedToken, model.Password);
            if (result.Succeeded)
            {
                TempData["SuccessMessage"] = "Password reset successful!";
                return RedirectToAction(nameof(Login));
            }

            foreach (var error in result.Errors)
                ModelState.AddModelError("", error.Description);

            return View(model);
        }

        // -------------------------------
        // OTHER
        // -------------------------------
        public IActionResult AccessDenied() => View();
        public IActionResult Lockout() => View();
    }
}
