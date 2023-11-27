﻿using System;

namespace cpaplib
{
	public class UserProfile : IComparable<UserProfile>
	{
		public int      UserProfileID { get; set; }
		public DateTime LastLogin     { get; set; } = DateTime.Now;
		public string   MachineID     { get; set; } = string.Empty;

		public string     UserName            { get; set; } = string.Empty;
		public string     FirstName           { get; set; } = string.Empty;
		public string     LastName            { get; set; } = string.Empty;
		public DateTime   DateOfBirth         { get; set; } = DateTime.Today.AddYears( -21 );
		public GenderType Gender              { get; set; } = GenderType.Male;
		public int        HeightInCentimeters { get; set; } = 178;
		public double     WeightInKilograms   { get; set; } = 80;

		public OperatingMode TherapyMode             { get; set; } = OperatingMode.APAP;
		public DateTime      DateOfDiagnosis         { get; set; } = DateTime.Today.AddYears( -1 );
		public double        UntreatedAHI            { get; set; } = 12;
		public double        PrescriptionPressureMin { get; set; } = 8;
		public double        PrescriptionPressureMax { get; set; } = 20;
		
		#region IComparable<UserProfile> interface implementation

		public int CompareTo( UserProfile other )
		{
			return other == null ? 0 : string.Compare( UserName, other.UserName, StringComparison.Ordinal );
		}
		
		#endregion
		
		#region Base class overrides

		public override string ToString()
		{
			return $"{UserName}, ID: {UserProfileID}";
		}

		#endregion 
	}


	public enum GenderType
	{
		Unspecified,
		Male,
		Female,
	}
}
