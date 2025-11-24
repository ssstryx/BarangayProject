// Models/CreateHouseholdVm.cs
using System.Collections.Generic;

namespace BarangayProject.Models
{
    // ViewModel for CreateHousehold form.
    // IMPORTANT: keep most properties nullable so they are not implicitly required
    // by model-binding/validation. Validate specific fields server-side where needed.
    public class CreateHouseholdVm
    {
        // Father (all optional => nullable)
        public string? FatherFirstName { get; set; }
        public string? FatherMiddleName { get; set; }
        public string? FatherLastName { get; set; }
        public string? FatherExtension { get; set; }
        public string? FatherOccupation { get; set; }
        public string? FatherEducation { get; set; }
        public string? FatherOccupationOther { get; set; }
        public string? FatherEducationOther { get; set; }

        // Sex default if provided, but property is nullable so binder won’t require it
        public string? FatherSex { get; set; } = "Male";

        // Mother (all optional => nullable)
        public string? MotherFirstName { get; set; }
        public string? MotherMiddleName { get; set; }
        public string? MotherLastName { get; set; }
        public string? MotherExtension { get; set; }
        public string? MotherOccupation { get; set; }
        public string? MotherEducation { get; set; }
        public string? MotherOccupationOther { get; set; }
        public string? MotherEducationOther { get; set; }

        public string? MotherSex { get; set; } = "Female";

        // Children - dynamic list (ChildVm properties made nullable below)
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
        public string? ToiletType { get; set; }
        public string? FoodProductionActivity { get; set; }
        public string? WaterSource { get; set; }

        // Sanitation "Other" details
        public string? ToiletTypeOther { get; set; }
        public string? FoodProductionActivityOther { get; set; }
        public string? WaterSourceOther { get; set; }

        // Health "Others feeding" specify
        public string? OthersFeedingSpecify { get; set; }
    }

    public class ChildVm
    {
        // Make these nullable so binder doesn't mark them required.
        public string? FirstName { get; set; }
        public string? MiddleName { get; set; }
        public string? LastName { get; set; }
        public string? Extension { get; set; }
        public string? Occupation { get; set; }
        public string? OccupationOther { get; set; }
        public string? Education { get; set; }
        public string? EducationOther { get; set; }
        public string? Sex { get; set; }
    }
}
