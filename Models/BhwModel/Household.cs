// Models/Household.cs
using BarangayProject.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using BarangayProject.Models.AdminModel;
namespace BarangayProject.Models.BhwModel
{
    public class Household
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [Display(Name = "Family Head")]
        public string FamilyHead { get; set; } = "";

        // Store full household form as JSON (optional)
        public string? Details { get; set; }

        public bool IsArchived { get; set; } = false;
        public DateTime? ArchivedAt { get; set; }
        public string ArchivedBy { get; set; } = "";

        // NEW: Sitio FK - optional (household may or may not have a sitio assigned)
        public int? SitioId { get; set; }

        [ForeignKey(nameof(SitioId))]
        public Sitio? Sitio { get; set; }

        // navigation
        public ICollection<Resident> Residents { get; set; } = new List<Resident>();
        public HouseholdHealth? Health { get; set; }
        public HouseholdSanitation? Sanitation { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
    }
}
