using System.ComponentModel.DataAnnotations;

namespace BarangayProject.Models
{
    public class ForgotPasswordViewModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; }
    }
}
