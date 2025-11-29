using BarangayProject.Models;
using System;
using System.Collections.Generic;
using BarangayProject.Models.AdminModel;
public class ReportsViewModel
{
    public int TotalUsers { get; set; }
    public int ActiveUsers { get; set; }
    public int InactiveUsers { get; set; }

    public int TotalSitios { get; set; }

    // chart-friendly data
    public List<(string Label, int Count)> UsersByMonth { get; set; } = new();
    public List<(string Label, int Count)> SitiosByAssignment { get; set; } = new();

    public List<DashboardViewModel> RecentActivities { get; set; } = new();
}
