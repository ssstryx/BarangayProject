// Models/Resident.cs
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BarangayProject.Models.BhwModel
{
    public class Resident
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int HouseholdId { get; set; }

        [ForeignKey(nameof(HouseholdId))]
        public Household Household { get; set; }

        [Required]
        public string Role { get; set; } = ""; // "Father", "Mother", "Child"

        [Required]
        public string FirstName { get; set; } = "";

        public string MiddleName { get; set; } = "";

        [Required]
        public string LastName { get; set; } = "";

        public string? Extension { get; set; }

        public string Sex { get; set; } = ""; // "Male" / "Female"

        public string Occupation { get; set; } = "";

        public string? OccupationOther { get; set; }

        public string Education { get; set; } = "";

        public string? EducationOther { get; set; }

        public DateTime? DateOfBirth { get; set; }
        public int? Age { get; set; }

        // NEW: track when this resident record was created (UTC)
        public DateTime? CreatedAt { get; set; }

        public bool? IsArchived { get; set; }
        public DateTime? ArchivedAt { get; set; }

    }
}
