namespace BarangayProject.Models.BhwModel
{
    public class ResidentListVm
    {
        public int Id { get; set; }
        public int? HouseholdId { get; set; }
        public string Name { get; set; } = "";
        public string Sex { get; set; } = "";
        public string Occupation { get; set; } = "";
        public string Education { get; set; } = "";
        public DateTime? DateOfBirth { get; set; }
        public int? Age { get; set; }
    }
}
