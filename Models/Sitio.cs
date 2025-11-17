using System.ComponentModel.DataAnnotations;

namespace BarangayProject.Models
{
    public class Sitio
    {
        public int Id { get; set; }

        [Required]
        [StringLength(150)]
        public string Name { get; set; } = "";

        // Location is now optional and not shown on Create UI, but kept in model for compatibility
        [StringLength(250)]
        public string? Location { get; set; }

        // NEW: assigned BHW (FK to AspNetUsers.Id, string)
        [StringLength(191)]
        public string? AssignedBhwId { get; set; }

        // navigation property to ApplicationUser (BHW)
        public ApplicationUser? AssignedBhw { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;


    }
}
