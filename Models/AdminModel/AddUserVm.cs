using System.ComponentModel.DataAnnotations;

namespace BarangayProject.Models
{
    public class AddUserVm
    {
        [Required]
        [Display(Name = "First Name")]
        public string FirstName { get; set; }

        [Display(Name = "Middle Name")]
        public string? MiddleName { get; set; } // <-- make nullable, no [Required]

        [Required]
        [Display(Name = "Last Name")]
        public string LastName { get; set; }

        [Required]
        [EmailAddress]
        public string Email { get; set; }

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; }

        [Required]
        public string Role { get; set; }

        [DataType(DataType.Password)]
        public string? ConfirmPassword { get; set; }

    }
}
