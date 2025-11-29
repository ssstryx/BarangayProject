// Models/SitioBhw.cs
using System;
using System.ComponentModel.DataAnnotations;

namespace BarangayProject.Models.AdminModel
{
    public class SitioBhw
    {
        // composite PK (SitioId + BhwId) configured in DbContext
        public int SitioId { get; set; }
        [Required]
        [StringLength(191)]
        public string BhwId { get; set; } = "";

        // navigation
        public Sitio? Sitio { get; set; }
        public ApplicationUser? Bhw { get; set; }

        public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    }
}
