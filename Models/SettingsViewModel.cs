namespace BarangayProject.Models;

public class SettingsViewModel
{
    public string? SystemName { get; set; }

    // This is the saved logo path shown in the UI (e.g. "/images/logo.png?v=12345")
    public string? LogoPath { get; set; }

    // File upload for new logo
    public IFormFile? SiteLogoUpload { get; set; }

    // for password reset block
    public string? ResetUserId { get; set; }
    public string? ResetNewPassword { get; set; }
    public string? ResetNewPasswordConfirm { get; set; }
}
