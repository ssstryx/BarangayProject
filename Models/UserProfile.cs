using System;

namespace BarangayProject.Models
{
    // Simple POCO representing the 1:1 profile for ApplicationUser
    public class UserProfile
    {
        public int Id { get; set; }
        public string UserId { get; set; }
        public ApplicationUser User { get; set; }
        public string FirstName { get; set; }
        public string? MiddleName { get; set; }
        public string LastName { get; set; }
        public DateTime? BirthDate { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ModifiedAt { get; set; }

        // Persistent numeric display id for UI (safe, non-breaking)
        public int? UserNumber { get; set; }

        // SitioId if you tie profiles to sitios
        public int SitioId { get; set; }
    }
}
