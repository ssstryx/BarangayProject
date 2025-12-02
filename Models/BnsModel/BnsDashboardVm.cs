namespace BarangayProject.Models.BnsModel
{
    public class BnsDashboardVm
    {
        public int TotalPopulation { get; set; }
        public int TotalHouseholds { get; set; }
        public int TotalFemale { get; set; }
        public int TotalMale { get; set; }

        public List<string>? TrendLabels { get; set; }
        public List<int>? TrendValues { get; set; }
    }
}
