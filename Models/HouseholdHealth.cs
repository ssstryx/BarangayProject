// Models/HouseholdHealth.cs
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BarangayProject.Models
{
    public class HouseholdHealth
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int HouseholdId { get; set; }

        [ForeignKey(nameof(HouseholdId))]
        public Household Household { get; set; }

        public bool MotherPregnant { get; set; }
        public bool FamilyPlanning { get; set; }
        public bool ExclusiveBreastfeeding { get; set; }
        public bool MixedFeeding { get; set; }
        public bool BottleFed { get; set; }
        public bool OthersFeeding { get; set; }
        public string? OthersFeedingSpecify { get; set; }

        public bool UsingIodizedSalt { get; set; }
        public bool UsingIFR { get; set; }
    }
}
