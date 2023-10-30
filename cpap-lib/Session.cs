﻿using System;
using System.Collections.Generic;

namespace cpaplib
{
	public class Session : IComparable<Session>
	{
		public DateTime StartTime { get; set; }
		public DateTime EndTime   { get; set; }
		public TimeSpan Duration  { get => EndTime - StartTime; }

		public string Source { get; set; }

		public SourceType SourceType   { get; set; } = SourceType.CPAP;

		public List<Signal> Signals { get; set; } = new List<Signal>();

		#region Public functions

		public static bool TimesOverlap( Session a, Session b )
		{
			return DateHelper.RangesOverlap( a.StartTime, a.EndTime, b.StartTime, b.EndTime );
		}

		public static bool TimesOverlap( Session session, Signal signal )
		{
			return DateHelper.RangesOverlap( session.StartTime, session.EndTime, signal.StartTime, signal.EndTime );
		}

		public Signal GetSignalByName( string name, StringComparison comparison = StringComparison.OrdinalIgnoreCase )
		{
			for( int i = 0; i < Signals.Count; i++ )
			{
				if( Signals[ i ].Name.Equals( name, comparison ) )
				{
					return Signals[ i ];
				}
			}

			return null;
		}

		internal void Merge( Session other )
		{
			if( StartTime > other.EndTime || EndTime < other.StartTime )
			{
				throw new Exception( $"Cannot merge sessions which do not overlap in time {StartTime:T} to {EndTime:T}" );
			}
			
			foreach( var signal in other.Signals )
			{
				var existingSignal = GetSignalByName( signal.Name );
				if( existingSignal != null )
				{
					// The code calling Merge should have decided what to do about duplicate signal names before calling this function.
					throw new Exception( $"The {nameof( Session )} already contains a {nameof( Signal )} named {signal.Name}" );
				}

				// We will create a clone and trim that, because it's conceivable (maybe even likely) that the caller
				// will still want the original Signal unchanged when this call is finished. 
				var trimmedSignal = signal.Clone();
				trimmedSignal.TrimToTime( StartTime, EndTime );
				
				Signals.Add( trimmedSignal );
			}
		}

		public void AddSignal( Signal signal )
		{
			if( GetSignalByName( signal.Name ) != null )
			{
				throw new Exception( $"This {nameof( Session )} already contains a signal named {signal.Name}" );
			}

			Signals.Add( signal );
			
			StartTime = DateUtil.Min( StartTime, signal.StartTime );
			EndTime   = DateUtil.Max( EndTime, signal.EndTime );
		}
		
		#endregion
		
		#region IComparable<Session> interface implementation 
		
		public int CompareTo( Session other )
		{
			return SourceType == other.SourceType ? StartTime.CompareTo( other.StartTime ) : SourceType.CompareTo( other.SourceType );
		}
		
		#endregion 

		#region Base class overrides

		public override string ToString()
		{
			return $"{StartTime.ToShortDateString()}    {StartTime.ToLongTimeString()} - {EndTime.ToLongTimeString()}";
		}

		#endregion
	}
}
