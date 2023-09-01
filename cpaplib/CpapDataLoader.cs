﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using StagPoint.EDF.Net;

namespace cpaplib
{
	public class CpapDataLoader
	{
		public MachineIdentification MachineID = new MachineIdentification();

		public List<DailyReport> Days { get; } = new List<DailyReport>();

		private static string[] expectedFiles = 
		{
			"STR.edf",
			"Identification.tgt",
		};

		private static string[] expectedFolders =
		{
			"SETTINGS",
			"DATALOG",
		};

		public void LoadFromFolder( string folderPath, DateTime? minDate = null, DateTime? maxDate = null )
		{
			EnsureCorrectFolderStructure( folderPath );
			LoadMachineIdentificationInfo( folderPath );

			var indexFilename = Path.Combine( folderPath, "STR.edf" );
			LoadIndexFile( indexFilename, minDate, maxDate );

			foreach( var day in Days )
			{
				LoadSessionData( folderPath, day );
			}
		}

		private void LoadMachineIdentificationInfo( string rootFolder )
		{
			var filename = Path.Combine( rootFolder, "Identification.tgt" );
			MachineID = MachineIdentification.ReadFrom( filename );
		}

		private void EnsureCorrectFolderStructure( string rootFolder )
		{
			foreach( var folder in expectedFolders )
			{
				var directoryPath = Path.Combine( rootFolder, folder );
				if( !Directory.Exists( directoryPath ) )
				{
					throw new DirectoryNotFoundException( $"Directory {directoryPath} does not exist" );
				}
			}

			foreach( var filename in expectedFiles )
			{
				var filePath = Path.Combine( rootFolder, filename );
				if( !File.Exists( filePath ) )
				{
					throw new FileNotFoundException( $"File {filePath} does not exist" );
				}
			}
		}

		private void LoadSessionData( string rootFolder, DailyReport day )
		{
			var logFolder = Path.Combine( rootFolder, $@"DATALOG\{day.Date:yyyyMMdd}" );
			if( !Directory.Exists( logFolder ) )
			{
				return;
			}

			var filenames = Directory.GetFiles( logFolder, "*.edf" );
			foreach( var filename in filenames )
			{
				var baseFilename = Path.GetFileNameWithoutExtension( filename );

				// EVE and CSL files are handled separately
				var ignoreThisFile =
					baseFilename.EndsWith( "_CSL", StringComparison.InvariantCultureIgnoreCase ) ||
					baseFilename.EndsWith( "_EVE", StringComparison.InvariantCultureIgnoreCase );
				if( ignoreThisFile )
				{
					continue;
				}

				var fileDate = DateTime
				               .ParseExact( baseFilename.Substring( 0, baseFilename.Length - 4 ), "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture )
				               .Trim( TimeSpan.TicksPerMinute );

				foreach( var session in day.Sessions )
				{
					// The start times will probably not match exactly, but also shouldn't differ by more than a minute.
					// Typically the difference will be that the session starts on an even minute boundary, and the 
					// file does not, so stripping the seconds from the file start time should make them match exactly. 
					if( !session.StartTime.Equals( fileDate ) )
					{
						continue;
					}

					using( var file = File.OpenRead( filename ) )
					{
						// Sigh. There are some EDF headers that have invalid values, and we need to check that first.
						var header = new EdfFileHeader( file );
						if( header.NumberOfDataRecords <= 0 )
						{
							break;
						}

						// Now rewind the file, because there's currently no way to "continue reading the file"
						file.Position = 0;

						// Read in the EDF file 
						var edf = new EdfFile();
						edf.ReadFrom( file );

						Debug.WriteLine( $"Attach session: {session.StartTime}  - {baseFilename}" );

						foreach( var signal in edf.Signals )
						{
							if( signal.Label.Value.StartsWith( "crc", StringComparison.InvariantCultureIgnoreCase ) )
							{
								continue;
							}

							session.AddSignal( session.StartTime, signal );

							Debug.WriteLine( $"        {signal.Label}" );
						}
					}

					break;
				}
			}
		}

		private void LoadIndexFile( string filename, DateTime? minDate, DateTime? maxDate )
		{
			var file = EdfFile.Open( filename );

			// Copy all raw and single-value settings 
			for( int i = 0; i < file.Signals[ 0 ].Samples.Count; i++ )
			{
				// Gather a hash table of settings for a single day from across the signals 
				var lookup = new Dictionary<string, double>();
				for( int j = 0; j < file.Signals.Count; j++ )
				{
					lookup[ file.Signals[ j ].Label ] = file.Signals[ j ].Samples[ i ];
				}

				// Read in and process the settings for a single day
				var settings = DailyReport.Read( lookup );

				Days.Add( settings );
			}

			// Mask On and Mask Off times are stored as the number of seconds since the day started.
			// Remember that according to ResMed, the day starts at 12pm (noon) instead of the more conventional 
			// and sane 12am (midnight).
			// There will be a maximum of ten MaskOn/MaskOff events per day (always true?)
			var maskOnSignal  = file.GetSignalByName( "MaskOn",  "Mask On" );
			var maskOffSignal = file.GetSignalByName( "MaskOff", "Mask Off" );

			// There will be an even number of MaskOn/MaskOff times for each day
			var numberOfEntriesPerDay = maskOnSignal.Samples.Count / Days.Count;
			Debug.Assert( maskOnSignal.Samples.Count % numberOfEntriesPerDay == 0, "Invalid calculation of Number of Sessions Per Day" );

			for( int dayIndex = 0; dayIndex < Days.Count; dayIndex++ )
			{
				var day = Days[ dayIndex ];

				if( day.Duration.TotalMinutes < 5 )
				{
					continue;
				}

				for( int i = 0; i < day.MaskEvents; i++ )
				{
					var sampleIndex = dayIndex * numberOfEntriesPerDay + i;

					// Stop processing MaskOn/MaskOff when we encounter a -1
					if( maskOnSignal.Samples[ sampleIndex ] < 0 )
					{
						break;
					}

					// Mask times are stored as the number of seconds since the "day" started. Remember that
					// the ResMed "day" starts at 12pm (noon) and continues until the next calendar day at 12pm.
					var maskOn  = day.Date.AddMinutes( maskOnSignal.Samples[ sampleIndex ] );
					var maskOff = day.Date.AddMinutes( maskOffSignal.Samples[ sampleIndex ] );

					// Discard empty sessions
					if( maskOn == maskOff )
					{
						continue;
					}

					var session = new MaskSession()
					{
						StartTime = maskOn,
						EndTime   = maskOff,
					};

					day.Sessions.Add( session );
				}
			}

			// Remove all days that are too short to be valid or are otherwise invalid
			RemoveInvalidDays();

			// Remove days that don't match the provided range. It's less efficient to do this after we've already 
			// gathered the basic day information, but it keeps the code much cleaner and more readable, and this 
			// isn't exactly a performance-critical section of code ;)
			FilterDaysByDate( minDate, maxDate );
		}

		private void FilterDaysByDate( DateTime? minDate, DateTime? maxDate )
		{
			int dayIndex = 0;
			if( minDate.HasValue || maxDate.HasValue )
			{
				while( dayIndex < Days.Count )
				{
					var date = Days[ dayIndex ].Date;

					if( minDate.HasValue && date < minDate )
					{
						Days.RemoveAt( dayIndex );
						continue;
					}

					if( maxDate.HasValue && date > maxDate )
					{
						Days.RemoveAt( dayIndex );
						continue;
					}

					dayIndex += 1;
				}
			}
		}

		private void RemoveInvalidDays()
		{
			int dayIndex = 0;
			while( dayIndex < Days.Count )
			{
				if( Days[ dayIndex ].Duration.TotalMinutes <= 5 )
				{
					Days.RemoveAt( dayIndex );
					continue;
				}

				dayIndex += 1;
			}
		}
	}
}