public class DashboardViewModel
{
    public int TotalUsers { get; set; }
    public int ActiveUsers { get; set; }
    public int InactiveUsers { get; set; }
    public int TotalSitios { get; set; }

    // Chart #1 – Users over last 6 months
    public List<(string Month, int Count)> UsersByMonth { get; set; } = new();

    // Chart #2 – Sitio assignment
    public List<(string Label, int Count)> SitiosByAssignment { get; set; } = new();

    // Chart #3 – Users by role
    public List<(string Role, int Count)> RolesByCount { get; set; } = new();


}
