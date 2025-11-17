using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BarangayProject.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BarangayProject.Services
{
    /// <summary>
    /// Background service that deletes old audit logs on a schedule.
    /// </summary>
    public class AuditCleanupService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<AuditCleanupService> _logger;
        private readonly TimeSpan _interval;
        private readonly TimeSpan _retention;

        public AuditCleanupService(IServiceProvider services,
                                   ILogger<AuditCleanupService> logger,
                                   IConfiguration configuration)
        {
            _services = services;
            _logger = logger;

            // read from configuration (optional) - fallback to defaults
            var intervalHours = configuration.GetValue<int?>("AuditCleanup:IntervalHours") ?? 24;
            var retentionDays = configuration.GetValue<int?>("AuditCleanup:RetentionDays") ?? 90;

            _interval = TimeSpan.FromHours(Math.Max(1, intervalHours)); // at least 1 hour
            _retention = TimeSpan.FromDays(Math.Max(1, retentionDays)); // at least 1 day
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // small startup delay to let app finish starting
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (TaskCanceledException) { return; }

            _logger.LogInformation("AuditCleanupService started. Interval={Interval} Retention={Retention} days",
                                   _interval, _retention.TotalDays);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await DoCleanup(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // shutting down, ignored
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AuditCleanupService encountered an error during cleanup.");
                }

                try
                {
                    await Task.Delay(_interval, stoppingToken);
                }
                catch (TaskCanceledException) { break; }
            }

            _logger.LogInformation("AuditCleanupService stopping.");
        }

        private async Task DoCleanup(CancellationToken cancellationToken)
        {
            // calculate cutoff in UTC
            var cutoff = DateTime.UtcNow - _retention;
            _logger.LogInformation("AuditCleanupService running cleanup. Removing audit logs older than {CutoffUtc}", cutoff);

            // create a scope to get a fresh ApplicationDbContext
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Use ExecuteSqlRawAsync to do efficient delete.
            // Parameterize cutoff to avoid SQL injection.
            var sql = "DELETE FROM AuditLogs WHERE EventTime < {0}";
            var rowsAffected = await db.Database.ExecuteSqlRawAsync(sql, new object[] { cutoff }, cancellationToken);

            _logger.LogInformation("AuditCleanupService deleted {Count} audit log rows older than {CutoffUtc}", rowsAffected, cutoff);
        }
    }
}
