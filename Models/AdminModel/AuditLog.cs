using System;

namespace BarangayProject.Models.AdminModel
{
    public class AuditLog
    {
        public long Id { get; set; }
        public DateTime EventTime { get; set; } = DateTime.UtcNow;

        // Admin user who performed the action (nullable)
        public string? UserId { get; set; }

        // Action name: "CreateUser", "DeleteSitio", "EditUser", "AssignBHW", ...
        public string? Action { get; set; }

        // Human readable details
        public string? Details { get; set; }

        // Entity type affected (optional but useful): "User", "Sitio"
        public string? EntityType { get; set; }

        // Entity id (string to support both numeric sitio id and AspNetUsers.Id)
        public string? EntityId { get; set; }

        // Optional JSON metadata if you want structured diff details
        public string? Metadata { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ModifiedAt { get; set; }
    }
}
