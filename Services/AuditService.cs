using System;
using System.Threading.Tasks;
using BarangayProject.Data;
using BarangayProject.Models.AdminModel;


namespace BarangayProject.Services
{
    public class AuditService
    {
        private readonly ApplicationDbContext _db;

        public AuditService(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task AddAsync(string action, string details, string? performedByUserId = null,
                                   string? entityType = null, string? entityId = null, string? metadataJson = null)
        {
            var a = new AuditLog
            {
                EventTime = DateTime.UtcNow,
                Action = action,
                Details = details,
                UserId = performedByUserId,
                EntityType = entityType,
                EntityId = entityId,
                Metadata = metadataJson,
                CreatedAt = DateTime.UtcNow
            };



            _db.AuditLogs.Add(a);
            await _db.SaveChangesAsync();
        }
    }
}
