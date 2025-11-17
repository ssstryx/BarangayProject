using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.Collections.Generic;

namespace BarangayProject.Models
{
    /// <summary>
    /// Central settings view model used by Admin controller and Settings view.
    /// Put this under Models to avoid naming collisions.
    /// </summary>
    public class SettingsViewModel
    {
        // Site identity
        public string SystemName { get; set; } = "Barangay System";
        public string? LogoPath { get; set; }

        // Upload
        public IFormFile? SiteLogoUpload { get; set; }

        // Password reset section
        public string? ResetUserId { get; set; }
        public string? ResetNewPassword { get; set; }
        public string? ResetNewPasswordConfirm { get; set; }
    }
}
