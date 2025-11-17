using System;
using System.Collections.Generic;

namespace BarangayProject.Models
{
    public class DashboardActivityVm
    {
        public string Description { get; set; } = "";
        public DateTime Timestamp { get; set; }
    }

    public class DashboardViewModel
    {
        public int TotalUsers { get; set; }
        public int ActiveUsers { get; set; }
        public int InactiveUsers { get; set; }
        public int TotalSitios { get; set; }

        public List<DashboardActivityVm> RecentActivities { get; set; } = new();
    }
}
