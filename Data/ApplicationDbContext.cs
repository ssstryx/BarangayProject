using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using BarangayProject.Models;

namespace BarangayProject.Data
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        // domain tables
        public DbSet<UserProfile> UserProfiles { get; set; }
        public DbSet<AuditLog> AuditLogs { get; set; }
        public DbSet<Sitio> Sitios { get; set; } = null!;
        public DbSet<SystemConfiguration> SystemConfigurations { get; set; }
        public DbSet<Household> Households { get; set; }
        public DbSet<Resident> Residents { get; set; }
        public DbSet<HouseholdHealth> HouseholdHealth { get; set; }
        public DbSet<HouseholdSanitation> HouseholdSanitation { get; set; }


        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // ---------- AuditLog configuration ----------
            builder.Entity<AuditLog>(entity =>
            {
                entity.ToTable("AuditLogs");
                entity.HasKey(a => a.Id);

                entity.Property(a => a.EventTime).HasColumnType("datetime(6)").IsRequired();
                entity.Property(a => a.UserId).HasMaxLength(450).IsRequired(false);
                entity.Property(a => a.Action).HasMaxLength(200).IsRequired(false);
                entity.Property(a => a.Details).HasColumnType("text").IsRequired(false);
                entity.Property(a => a.EntityType).HasMaxLength(100).IsRequired(false);
                entity.Property(a => a.EntityId).HasMaxLength(191).IsRequired(false);
                entity.Property(a => a.Metadata).HasColumnType("text").IsRequired(false);
                entity.HasIndex(a => new { a.UserId, a.EventTime });

                // Optional: if you want to keep a nullable link to the ApplicationUser, use a shadow FK and set delete behavior to SetNull.
                // This ensures if the user account gets deleted the audit row remains and ApplicationUserId becomes NULL.
                entity.Property<string>("ApplicationUserId").HasMaxLength(450).IsRequired(false);

                entity.HasOne<ApplicationUser>()              // NO relation to Sitio or other domain entities here
                      .WithMany(u => u.AuditLogs)            // only if ApplicationUser has ICollection<AuditLog> AuditLogs
                      .HasForeignKey("ApplicationUserId")
                      .OnDelete(DeleteBehavior.SetNull);
            });




            // Sitio mapping
            builder.Entity<Sitio>(entity =>
            {
                entity.ToTable("Sitios");
                entity.HasKey(s => s.Id);
                entity.Property(s => s.Name).HasMaxLength(150).IsRequired();
                entity.Property(s => s.Location).HasMaxLength(250).IsRequired(false);
                entity.Property(s => s.AssignedBhwId).HasMaxLength(450).IsRequired(false);

                entity.HasOne(s => s.AssignedBhw)
                      .WithMany(u => u.AssignedSitios) // optional navigation on ApplicationUser
                      .HasForeignKey(s => s.AssignedBhwId)
                      .OnDelete(DeleteBehavior.SetNull);
            });


            // Identity tables name tuning (optional)
            builder.Entity<ApplicationUser>().ToTable("AspNetUsers");
            builder.Entity<Microsoft.AspNetCore.Identity.IdentityRole>().ToTable("AspNetRoles");
            builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserRole<string>>().ToTable("AspNetUserRoles");
            builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<string>>().ToTable("AspNetUserClaims");
            builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<string>>().ToTable("AspNetUserLogins");
            builder.Entity<Microsoft.AspNetCore.Identity.IdentityRoleClaim<string>>().ToTable("AspNetRoleClaims");
            builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<string>>().ToTable("AspNetUserTokens");

            // Global conventions for string column lengths (MySQL safe)
            foreach (var entity in builder.Model.GetEntityTypes())
            {
                foreach (var property in entity.GetProperties().Where(p => p.ClrType == typeof(string)))
                {
                    if (property.GetMaxLength() == null)
                        property.SetMaxLength(191);
                }
            }

            // -------- UserProfile mapping: explicit 1:1 relationship --------
            builder.Entity<UserProfile>(entity =>
            {
                entity.ToTable("UserProfiles");
                entity.HasKey(p => p.Id);

                entity.Property(p => p.UserId).HasMaxLength(450).IsRequired();
                entity.Property(p => p.FirstName).HasMaxLength(100).IsRequired(false);
                entity.Property(p => p.MiddleName).HasMaxLength(100).IsRequired(false);
                entity.Property(p => p.LastName).HasMaxLength(100).IsRequired(false);
                entity.Property(p => p.BirthDate).HasColumnType("date").IsRequired(false);
                entity.Property(p => p.CreatedAt).HasColumnType("datetime(6)").IsRequired();
                entity.Property(p => p.ModifiedAt).HasColumnType("datetime(6)").IsRequired(false);

                // IMPORTANT: map the 1:1 relationship using the exact navigation properties
                // UserProfile.User (nav) <-> ApplicationUser.Profile (nav), FK = UserProfile.UserId
                entity.HasOne(p => p.User)
                      .WithOne(u => u.Profile)
                      .HasForeignKey<UserProfile>(p => p.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // Tune Identity string lengths to avoid index issues on MySQL
            builder.Entity<ApplicationUser>(entity =>
            {
                entity.Property(e => e.NormalizedEmail).HasMaxLength(191);
                entity.Property(e => e.NormalizedUserName).HasMaxLength(191);
            });
        }



        // set CreatedAt/ModifiedAt automatically if entity implements IAuditable
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var utcNow = DateTime.UtcNow;

            foreach (var entry in ChangeTracker.Entries()
                         .Where(e => e.Entity is IAuditable &&
                                (e.State == EntityState.Added || e.State == EntityState.Modified)))
            {
                var auditable = (IAuditable)entry.Entity;
                if (entry.State == EntityState.Added)
                {
                    auditable.CreatedAt = utcNow;
                    auditable.ModifiedAt = null;
                }
                else if (entry.State == EntityState.Modified)
                {
                    auditable.ModifiedAt = utcNow;
                }
            }

            return base.SaveChangesAsync(cancellationToken);
        }

    }
}
