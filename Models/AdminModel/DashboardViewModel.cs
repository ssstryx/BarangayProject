

namespace BarangayProject.Models.AdminModel
{
    public class DashboardViewModel
    {
        public int TotalUsers { get; set; }
        public int ActiveUsers { get; set; }
        public int InactiveUsers { get; set; }
        public int TotalSitios { get; set; }

        public DateTime Timestamp { get; set; }
        public string Action { get; set; } = "";
        // keep both names so older code still compiles
        public string Details { get; set; } = "";
        public string Description
        {
            get => string.IsNullOrWhiteSpace(Details) ? "" : Details;
            set => Details = value ?? "";
        }

        public List<DashboardViewModel> RecentActivities { get; set; } = new();
    }
}
