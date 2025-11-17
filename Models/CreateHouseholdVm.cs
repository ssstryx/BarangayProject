// Models/CreateHouseholdVm.cs
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace BarangayProject.Models
{
    public class CreateHouseholdVm
    {
        // Father
        public string FatherFirstName { get; set; }
        public string FatherMiddleName { get; set; }
        public string FatherLastName { get; set; }
        public string FatherExtension { get; set; }
        public string FatherOccupation { get; set; }
        public string FatherEducation { get; set; }
        public string FatherGender { get; set; } = "Male";

        // Mother
        public string MotherFirstName { get; set; }
        public string MotherMiddleName { get; set; }
        public string MotherLastName { get; set; }
        public string MotherExtension { get; set; }
        public string MotherOccupation { get; set; }
        public string MotherEducation { get; set; }
        public string MotherGender { get; set; } = "Female";

        // Children - dynamic list; the view will post indexed fields e.g. Children[0].FirstName
        public List<ChildVm> Children { get; set; } = new();

        // Health checks (booleans)
        public bool MotherPregnant { get; set; }
        public bool FamilyPlanning { get; set; }
        public bool ExclusiveBreastfeeding { get; set; }
        public bool MixedFeeding { get; set; }
        public bool BottleFed { get; set; }
        public bool OthersFeeding { get; set; }

        public bool UsingIodizedSalt { get; set; }
        public bool UsingIFR { get; set; }

        // Sanitation
        public string ToiletType { get; set; }
        public string FoodProductionActivity { get; set; }
        public string WaterSource { get; set; }
    }

    public class ChildVm
    {
        public string FirstName { get; set; }
        public string MiddleName { get; set; }
        public string LastName { get; set; }
        public string Extension { get; set; }
        public string Occupation { get; set; }
        public string Education { get; set; }
        public string Gender { get; set; }
    }
}
