using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using BarangayProject.Models.AdminModel;
using BarangayProject.Models.BhwModel;

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
        public DbSet<SitioBhw> SitioBhws { get; set; } = null!; // NEW: join table for many-to-many Sitio <-> BHW
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

                entity.HasOne<ApplicationUser>()              // NO relation required on ApplicationUser
                      .WithMany(u => u.AuditLogs)            // only if ApplicationUser has ICollection<AuditLog> AuditLogs
                      .HasForeignKey("ApplicationUserId")
                      .OnDelete(DeleteBehavior.SetNull);
            });

            // Household mapping: optional Sitio relationship
            builder.Entity<Household>(entity =>
            {
                entity.ToTable("Households");
                entity.HasKey(h => h.Id);

                entity.Property(h => h.FamilyHead).HasMaxLength(191).IsRequired();
                entity.Property(h => h.Details).HasColumnType("text").IsRequired(false);
                entity.Property(h => h.IsArchived).IsRequired();
                entity.Property(h => h.ArchivedAt).HasColumnType("datetime(6)").IsRequired(false);
                entity.Property(h => h.ArchivedBy).HasMaxLength(191).IsRequired(false);
                entity.Property(h => h.CreatedAt).HasColumnType("datetime(6)").IsRequired();
                entity.Property(h => h.UpdatedAt).HasColumnType("datetime(6)").IsRequired(false);

                // Sitio FK (optional)
                entity.HasOne(h => h.Sitio)
                      .WithMany(s => s.Households)
                      .HasForeignKey(h => h.SitioId)
                      .OnDelete(DeleteBehavior.SetNull);
            });

            // Sitio mapping
            builder.Entity<Sitio>(entity =>
            {
                entity.ToTable("Sitios");
                entity.HasKey(s => s.Id);
                entity.Property(s => s.Name).HasMaxLength(150).IsRequired();

                // NOTE: we removed the single AssignedBhwId column mapping here in favor of a join table SitioBhws.
                // If you decide to keep AssignedBhwId for backward compatibility, re-add property mapping below and handle it accordingly.
            });

            // NEW: SitioBhw join table mapping (many-to-many Sitio <-> BHW (ApplicationUser))
            builder.Entity<SitioBhw>(entity =>
            {
                entity.ToTable("SitioBhws");

                // Composite PK: SitioId + BhwId
                entity.HasKey(sb => new { sb.SitioId, sb.BhwId });

                entity.Property(sb => sb.SitioId).IsRequired();
                entity.Property(sb => sb.BhwId).HasMaxLength(450).IsRequired();

                entity.Property(sb => sb.AssignedAt).HasColumnType("datetime(6)").IsRequired();

                // Relationship to Sitio
                entity.HasOne(sb => sb.Sitio)
                      .WithMany(s => s.SitioBhws)
                      .HasForeignKey(sb => sb.SitioId)
                      .OnDelete(DeleteBehavior.Cascade);

                // Relationship to ApplicationUser (BHW). We use .WithMany() (no navigation required on ApplicationUser),
                // but if you add ICollection<SitioBhw> SitioBhws on ApplicationUser you can replace .WithMany() with .WithMany(u => u.SitioBhws)
                entity.HasOne(sb => sb.Bhw)
                      .WithMany() // or .WithMany(u => u.SitioBhws) if ApplicationUser defines that navigation
                      .HasForeignKey(sb => sb.BhwId)
                      .OnDelete(DeleteBehavior.Cascade);
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
