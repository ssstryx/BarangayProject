using System;
using System.ComponentModel.DataAnnotations;

namespace BarangayProject.Models.AdminModel
{
    public class SystemConfiguration
    {
        [Key]
        public int Id { get; set; }

        public string SiteName { get; set; } = "";
        public string? LogoFileName { get; set; }
        public string ThemeName { get; set; } = "default";
        public byte MaintenanceMode { get; set; } = 0;
        public byte SidebarCompact { get; set; } = 0;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ModifiedAt { get; set; }
    }
}
