﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace cpaplib
{
	public class StatCalculator
	{
		private Sorter _sorter;

		public StatCalculator( int initialCapacity )
		{
			_sorter = new Sorter( initialCapacity );
		}

		public SignalStatistics CalculateStats( string signalName, List<Session> sessions )
		{
			// Reset the _sorter for the next iteration 
			_sorter.Clear();

			// Make sure that there is at least one session which contains the named Signal
			if( sessions.All( x => x.GetSignalByName( signalName ) == null ) )
			{
				throw new IndexOutOfRangeException( $"Failed to find signal {signalName}" );
			}

			// Copy all available samples from all sessions into a single array that can be sorted and
			// used to calculate the statistics. 
			foreach( var session in sessions )
			{
				var signal = session.GetSignalByName( signalName );
				if( signal != null )
				{
					_sorter.AddRange( signal.Samples );
				}
			}

			// Sort the aggregated samples and calculate statistics on the results 
			var sortedSamples = _sorter.Sort();
			var bufferLength  = sortedSamples.Count;

			var stats = new SignalStatistics
			{
				SignalName   = signalName,
				Minimum      = sortedSamples[ (int)(bufferLength * 0.01) ],
				Average      = sortedSamples.Average(),
				Maximum      = sortedSamples.Max(),
				Median       = sortedSamples[ bufferLength / 2 ],
				Percentile95 = sortedSamples[ (int)(bufferLength * 0.95) ],
				Percentile99 = sortedSamples[ (int)(bufferLength * 0.995) ],
			};
				
			return stats;
		}
	}
}