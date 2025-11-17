// Models/Household.cs
using System.ComponentModel.DataAnnotations;

namespace BarangayProject.Models
{
    public class Household
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [Display(Name = "Family Head")]
        public string FamilyHead { get; set; }

        // Store full household form as JSON (optional, allows future expansion)
        public string Details { get; set; }
    }
}
