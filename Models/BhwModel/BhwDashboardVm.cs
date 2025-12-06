using BarangayProject.Models.AdminModel;
using System.Collections.Generic;

namespace BarangayProject.Models.BhwModel
{
    public class BhwDashboardVm
    {
        public string? UserEmail { get; set; }
        public int TotalPopulation { get; set; }
        public int TotalFamilies { get; set; }
        public int TotalFemale { get; set; }
        public int TotalMale { get; set; }

        // recent activity items
        public List<DashboardViewModel> RecentActivities { get; set; } = new();

        // trend data produced by controller
        public List<string> TrendLabels { get; set; } = new();
        public List<int> TrendValues { get; set; } = new();
    }
}
