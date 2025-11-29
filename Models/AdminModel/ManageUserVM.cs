using System;

namespace BarangayProject.Models.AdminModel
{
    public class ManageUserVm
    {
        public string UserId { get; set; }
        public int? UserNumber { get; set; }

        // Friendly display id shown in UI as plain number (e.g. "1", "2", "3")
        // If UserNumber is missing, fallback to short GUID snippet
        public string DisplayId
        {
            get
            {
                if (UserNumber.HasValue) return UserNumber.Value.ToString();
                return (UserId ?? "").Length >= 8 ? UserId.Substring(0, 8) : (UserId ?? "-");
            }
        }

        public string Email { get; set; }
        public string FullName { get; set; }          // from UserProfile
        public string Role { get; set; }              // primary role or "-"
        public bool IsLockedOut { get; set; }         // true if currently locked out / deactivated
        public DateTime? Joined { get; set; }         // from profile CreatedAt if available
    }
}
