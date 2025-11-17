using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Identity;

namespace BarangayProject.Models
{
    public class ApplicationUser : IdentityUser, IAuditable
    {
        public string DisplayName { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ModifiedAt { get; set; }
        public bool IsActive { get; set; } = true;

        // Navigation property to the profile
        public UserProfile Profile { get; set; }

        // ** Sitio assignment (optional per user) **
        // Add these two properties so EF knows users can be assigned to a Sitio
        public int? SitioId { get; set; }         // FK column (nullable)
        public Sitio Sitio { get; set; }          // navigation property

        // Optionally keep audit logs navigation if you moved AuditLog to Models
        public ICollection<AuditLog> AuditLogs { get; set; }

        // NEW: reverse navigation for Sitios assigned to this BHW
        // (used if your DbContext mapping uses .WithMany(u => u.AssignedSitios))
        public ICollection<Sitio>? AssignedSitios { get; set; }

        public string GetFullName()
        {
            if (Profile != null)
            {
                var first = Profile.FirstName ?? string.Empty;
                var last = Profile.LastName ?? string.Empty;
                var name = $"{first} {last}".Trim();
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
            return DisplayName ?? Email ?? UserName;
        }
    }

    // small interface used by your DbContext SaveChanges logic
    public interface IAuditable
    {
        DateTime CreatedAt { get; set; }
        DateTime? ModifiedAt { get; set; }
    }
}
