﻿using System.Linq;

namespace cpaplib
{
	
	/// <summary>
	/// Note that the order in which these values are defined is also the order in which the associated
	/// events are sorted. 
	/// </summary>
	public enum EventType
	{
		/// <summary>
		/// Indicates the time when recording starts 
		/// </summary>
		RecordingStarts,
		/// <summary>
		/// Indicates the time when recording ends
		/// </summary>
		RecordingEnds,
		/// <summary>
		/// An apnea caused by an airway obstruction 
		/// </summary>
		ObstructiveApnea,
		/// <summary>
		/// An apnea caused by a partially obstructed airway
		/// </summary>
		Hypopnea,
		/// <summary>
		/// An apnea where the airway is unobstructed. Also often called a "Central Apnea", it indicates a
		/// lack of breathing despite the lack of a detectable obstruction. 
		/// </summary>
		ClearAirway,
		/// <summary>
		/// Respiratory Effort Related Arousal (RERA) is a period of increased respiratory effort leading to an arousal
		/// </summary>
		RERA,
		/// <summary>
		/// A period of Cheyne-Stokes Respiration
		/// </summary>
		CSR,
		/// <summary>
		/// A reduction in airflow of between 25% and 50% and lasting for at least 1 second
		/// </summary>
		FlowLimitation,
		/// <summary>
		/// An apnea event whose classification could not be determined (neither obstructive nor central)
		/// </summary>
		Unclassified,
		/// <summary>
		/// A full or partial arousal not related to respiratory effort
		/// </summary>
		Arousal,
		/// <summary>
		/// A period of pressure leakage above 20 L/min lasting at least one second 
		/// </summary>
		LargeLeak,
		/// <summary>
		/// A period of three or more episodes of Central Apnea lasting more than three seconds,
		/// separated by no more than 20 seconds of normal breathing. 
		/// </summary>
		PeriodicBreathing,
		VariableBreathing,
		BreathingNotDetected,
		/// <summary>
		/// The vibratory snore index measures the strength of the snoring vibrations
		/// </summary>
		VibratorySnore,
		/// <summary>
		/// A period of low blood oxygen saturation (less than 90%) lasting for at least 1 second
		/// </summary>
		Hypoxemia,
		/// <summary>
		/// A drop in blood oxygen saturation of at least 4% below baseline, lasting at least 10 seconds
		/// </summary>
		Desaturation,
		/// <summary>
		/// A period of pulse rate above 100 beat/minute
		/// </summary>
		Tachycardia,
		/// <summary>
		/// A period of pulse rate below 50 beats/minute
		/// </summary>
		Bradycardia,
		/// <summary>
		/// Pulse rate exceeded the threshold difference from baseline
		/// </summary>
		PulseRateChange,
	}

	/// <summary>
	/// Groups EventType values into logical groups 
	/// </summary>
	public static class EventTypes
	{
		public static readonly EventType[] Apneas =
		{
			EventType.ObstructiveApnea,
			EventType.Hypopnea,
			EventType.ClearAirway,
			EventType.Unclassified,
		};

		public static readonly EventType[] RespiratoryDisturbance =
		{
			EventType.ObstructiveApnea,
			EventType.Hypopnea,
			EventType.ClearAirway,
			EventType.Unclassified,
			EventType.RERA,
		};

		public static readonly EventType[] OxygenSaturation =
		{
			EventType.Desaturation,
			EventType.Hypoxemia,
		};

		public static readonly EventType[] Pulse =
		{
			EventType.Bradycardia,
			EventType.Tachycardia,
			EventType.PulseRateChange,
		};

		public static readonly EventType[] Breathing =
		{
			EventType.CSR,
			EventType.PeriodicBreathing,
			EventType.VariableBreathing,
			EventType.BreathingNotDetected,
		};

		public static readonly EventType[] CPAP =
			RespiratoryDisturbance
				.Concat( new EventType[]
				{
					EventType.CSR
				} )
				.Concat( OxygenSaturation )
				.Concat( Pulse )
				.ToArray();
	}
}
