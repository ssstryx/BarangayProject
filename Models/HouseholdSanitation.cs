// Models/HouseholdSanitation.cs
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BarangayProject.Models
{
    public class HouseholdSanitation
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int HouseholdId { get; set; }

        [ForeignKey(nameof(HouseholdId))]
        public Household Household { get; set; }

        public string? ToiletType { get; set; }
        public string? ToiletTypeOther { get; set; }

        public string? FoodProductionActivity { get; set; }
        public string? FoodProductionActivityOther { get; set; }

        public string? WaterSource { get; set; }
        public string? WaterSourceOther { get; set; }
    }
}
