// Models/Sitio.cs
using BarangayProject.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using BarangayProject.Models.BhwModel;

namespace BarangayProject.Models.AdminModel
{
    public class Sitio
    {
        public int Id { get; set; }

        [Required]
        [StringLength(150)]
        public string Name { get; set; } = "";

        // --- Many-to-many join collection ---
        // This is the canonical relation now: Sitio <-> SitioBhw -> ApplicationUser (BHW)
        public ICollection<SitioBhw> SitioBhws { get; set; } = new List<SitioBhw>();

        // navigation: households in this sitio
        public ICollection<Household> Households { get; set; } = new List<Household>();

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // -------------------------
        // Backwards-compatibility helpers (NOT mapped to DB)
        // so existing code referencing AssignedBhw / AssignedBhwId keeps compiling.
        // They return the *first* assigned BHW (or null) if any assignments exist.
        // Prefer to migrate code to use Sitio.SitioBhws (multiple BHWs) later.
        // -------------------------
        [NotMapped]
        public string? AssignedBhwId
        {
            get
            {
                // first assignment's BhwId (if any)
                return SitioBhws?.FirstOrDefault()?.BhwId;
            }
            // we keep a setter to ease model-binding scenarios in legacy create/edit views
            set
            {
                // setter intentionally left blank — actual persistence should go through SitioBhws join table
                // Controller compatibility code may still create SitioBhw rows after sitio is created.
            }
        }

        [NotMapped]
        public ApplicationUser? AssignedBhw
        {
            get
            {
                return SitioBhws?.FirstOrDefault()?.Bhw;
            }
            // no setter
        }

        [NotMapped]
        public string AssignedBhwDisplay
        {
            get
            {
                var u = AssignedBhw;
                if (u != null)
                {
                    if (!string.IsNullOrWhiteSpace(u.DisplayName)) return u.DisplayName;
                    if (!string.IsNullOrWhiteSpace(u.UserName)) return u.UserName;
                }

                var id = AssignedBhwId;
                return string.IsNullOrWhiteSpace(id) ? "" : id;
            }
        }
    }
}
