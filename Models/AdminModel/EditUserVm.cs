using System;
using System.ComponentModel.DataAnnotations;

namespace BarangayProject.Models
{
    public class EditUserVm
    {
        // This is required by the AdminController code (hidden id field)
        public string UserId { get; set; } = "";

        [Required, EmailAddress]
        public string Email { get; set; } = "";

        [Required]
        public string FirstName { get; set; } = "";

        // Middle name optional — nullable so not required
        public string? MiddleName { get; set; }

        [Required]
        public string LastName { get; set; } = "";

        // Role chosen from UI (can be empty string)
        public string Role { get; set; } = "";
    }
}
