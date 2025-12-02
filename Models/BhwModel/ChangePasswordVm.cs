using System.ComponentModel.DataAnnotations;

namespace BarangayProject.Models.BhwModel
{
    public class ChangePasswordVm
    {
        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Current password")]
        public string CurrentPassword { get; set; }

        [Required]
        [DataType(DataType.Password)]
        [MinLength(8, ErrorMessage = "The new password must be at least {1} characters long.")]
        [Display(Name = "New password")]
        public string NewPassword { get; set; }

        [Required]
        [DataType(DataType.Password)]
        [Compare(nameof(NewPassword), ErrorMessage = "The new password and confirmation do not match.")]
        [Display(Name = "Confirm new password")]
        public string ConfirmPassword { get; set; }
    }
}
