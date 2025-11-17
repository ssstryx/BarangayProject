namespace BarangayProject.Models
{
    public class BhwDashboardVm
    {
        public string UserEmail { get; set; }

        // Dashboard stats
        public int TotalPopulation { get; set; }
        public int TotalHouseholds { get; set; }
        public int TotalFemale { get; set; }
        public int TotalMale { get; set; }
    }
}
