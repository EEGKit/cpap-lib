﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using StagPoint.EDF.Net;

// ReSharper disable ConvertToUsingDeclaration

namespace cpaplib
{
	public class ResMedDataLoader
	{
		private static string[] expectedFiles = new[]
		{
			"STR.edf",
			"Identification.tgt",
		};

		private static string[] expectedFolders = new[]
		{
			// "SETTINGS",
			"DATALOG",
		};
		
		private static Dictionary<int, OperatingMode> s_modeMapping = new Dictionary<int, OperatingMode>()
		{
			{ 11, OperatingMode.APAP }, // "For Her" model, which is just AutoCPAP with custom algorithms
			{ 9, OperatingMode.AVAPS },
			{ 8, OperatingMode.ASV_VARIABLE_EPAP },
			{ 7, OperatingMode.ASV },
			{ 6, OperatingMode.BILEVEL_AUTO_FIXED_PS },
			{ 5, OperatingMode.BILEVEL_FIXED },
			{ 4, OperatingMode.BILEVEL_FIXED },
			{ 3, OperatingMode.BILEVEL_FIXED },
			{ 2, OperatingMode.BILEVEL_FIXED },
			{ 1, OperatingMode.APAP },
			{ 0, OperatingMode.CPAP },
		};

		private MachineIdentification _machineInfo    = new MachineIdentification();
		private TimeSpan              _timeAdjustment = TimeSpan.Zero;

		public List<DailyReport> LoadFromFolder( string folderPath, DateTime? minDate = null, DateTime? maxDate = null, TimeSpan? timeAdjustment = null )
		{
			if( timeAdjustment.HasValue )
			{
				_timeAdjustment = (TimeSpan)timeAdjustment;
			}
			
			EnsureCorrectFolderStructure( folderPath );

			_machineInfo = LoadMachineIdentificationInfo( folderPath );

			var indexFilename = Path.Combine( folderPath, "STR.edf" );
			var days          = LoadIndexAndSettings( indexFilename, minDate ?? DateTime.MinValue, maxDate ?? DateTime.MaxValue );
			
#if IMPORT_ASYNC
			var tasks = new Task[ days.Count ];

			for( int i = 0; i < days.Count; i++ )
			{
				var day = days[ i ];

				tasks[ i ] = Task.Run( () =>
				{
					// Loads all event and session data for the given day
					ImportSessionsAndEvents( folderPath, day );
				} );
			}

			Task.WaitAll( tasks );
#else
			foreach( var day in days )
			{
				// Loads all event and session data for the given day
				LoadSessionsForDay( folderPath, day );
			}
#endif
			
			// Make sure that each Session has its Source set. Sessions may be created by other processes, such as
			// pulse oximeter import, etc., and this will help to differentiate them. 
			foreach( var day in days )
			{
				foreach( var session in day.Sessions )
				{
					session.Source = _machineInfo.ProductName;
				}
			}
			
			// Fix up the ReportDate field to eliminate the stupid 12 hour time offset added by the ResMed machine. 
			// It was important (or at least conveniently useful) to retain it during some phases of the import process,
			// but now import is done and we don't need that offset anymore.
			foreach( var day in days )
			{
				day.ReportDate = day.ReportDate.Date;
			}

			return days;
		}

		public static MachineIdentification LoadMachineIdentificationInfo( string rootFolder )
		{
			var filename    = Path.Combine( rootFolder, "Identification.tgt" );
			var machineInfo = new MachineIdentification();

			using( var file = File.OpenRead( filename ) )
			{
				using( var reader = new StreamReader( file ) )
				{
					while( !reader.EndOfStream )
					{
						var line = reader.ReadLine()?.Trim();
						if( string.IsNullOrEmpty( line ) || !line.StartsWith( "#", StringComparison.Ordinal ) )
						{
							continue;
						}

						int spaceIndex = line.IndexOf( " ", StringComparison.Ordinal );
						Debug.Assert( spaceIndex != -1 );

						var key   = line.Substring( 1, spaceIndex - 1 );
						var value = line.Substring( spaceIndex + 1 ).Trim().Replace( '_', ' ' );

						machineInfo.Fields[ key ] = value;
					}

					machineInfo.ProductName  = machineInfo.Fields[ "PNA" ];
					machineInfo.SerialNumber = machineInfo.Fields[ "SRN" ];
					machineInfo.ModelNumber  = machineInfo.Fields[ "PCD" ];
				}
			}

			return machineInfo;
		}

		public static bool HasCorrectFolderStructure( string rootFolder )
		{
			foreach( var folder in expectedFolders )
			{
				var directoryPath = Path.Combine( rootFolder, folder );
				if( !Directory.Exists( directoryPath ) )
				{
					return false;
				}
			}

			foreach( var filename in expectedFiles )
			{
				var filePath = Path.Combine( rootFolder, filename );
				if( !File.Exists( filePath ) )
				{
					return false;
				}
			}

			return true;
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

		private void ImportSessionsAndEvents( string rootFolder, DailyReport day )
		{
			var logFolder = Path.Combine( rootFolder, $@"DATALOG\{day.ReportDate:yyyyMMdd}" );
			if( !Directory.Exists( logFolder ) )
			{
				// TODO: How to better handle dates where the SD Card wasn't inserted?
				
				// The only reason I've seen so far for the session directory to not exist for a given day is when
				// the user has forgotten to put the SD Card back into the machine. On such days, you will still 
				// have summary information available (such as settings, AHI, mask times, etc), but no graph data
				// will be found. 
				// 
				// For now, these dates are not even imported. This needs to get fixed. 
				
				Debug.WriteLine( $"Could not find the session directory for {logFolder}. Likely missing SD Card storage for this day." );
				
				return;
			}

			LoadEventsAndAnnotations( logFolder, day );
			LoadCheyneStokesEvents( logFolder, day );

			var filenames = Directory.GetFiles( logFolder, "*.edf" );
			
			// I believe the filenames are already sorted, but since it's important that they are sorted by date
			// and time, we'll explicitly sort them. Note that this coincidentally sorts the high-resolution 
			// signals before the low-resolution signals, which is a nice bonus.
			Array.Sort( filenames );
			
			foreach( var filename in filenames )
			{
				var baseFilename = Path.GetFileNameWithoutExtension( filename );

				// EVE and CSL files are handled separately
				var ignoreThisFile =
					baseFilename.EndsWith( "_CSL", StringComparison.OrdinalIgnoreCase ) ||
					baseFilename.EndsWith( "_EVE", StringComparison.OrdinalIgnoreCase );
				if( ignoreThisFile )
				{
					continue;
				}

				// The file's date, extracted from the filename, will be used to search for the correct session.
				// The date/time is incorrect for any other purpose (I think)
				var fileDate = DateTime
				               .ParseExact( baseFilename.Substring( 0, baseFilename.Length - 4 ), "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture )
				               .Trim( TimeSpan.TicksPerMinute );

				int sessionIndex = 0;
				while( sessionIndex < day.Sessions.Count )
				{
					var session = day.Sessions[ sessionIndex++];
					
					// If the file's extracted date does not intersect with the session, then it's not relevant to that session 
					if( session.StartTime > fileDate || session.EndTime < fileDate )
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
						
						// Now rewind the file, because there's currently no way to "continue reading the file from here"
						file.Position = 0;

						// Read in the EDF file 
						var edf = new EdfFile();
						edf.ReadFrom( file );

						foreach( var signal in edf.Signals )
						{
							var signalName = SignalNames.GetStandardName( signal.Label.Value );
							
							// We don't need to keep the CRC checksum signals 
							if( signalName.StartsWith( "crc", StringComparison.InvariantCultureIgnoreCase ) )
							{
								continue;
							}

							if( signalName.Equals( SignalNames.MaskPressureLow, StringComparison.Ordinal ) )
							{
								// If we already have high-resolution Mask Pressure signal data, ignore the low-resolution 
								if( session.Signals.Any( x => x.Name == SignalNames.MaskPressure ) )
								{
									continue;
								}

								// Otherwise, rename the low-resolution mask pressure data for consistency 
								signalName = signal.Label.Value = SignalNames.MaskPressure;
							}
							
							// Some signals should be scaled to match the other signals
							var scaleToLitersPerMinute = signalName.Equals( SignalNames.FlowRate, StringComparison.Ordinal ) ||
							                             signalName.Equals( SignalNames.LeakRate, StringComparison.Ordinal );
							if( scaleToLitersPerMinute )
							{
								// Convert from L/s to L/m
								signal.PhysicalDimension.Value =  "L/min";
								signal.PhysicalMaximum.Value   *= 60;
								signal.PhysicalMinimum.Value   *= 60;
								
								for( int i = 0; i < signal.Samples.Count; i++ )
								{
									signal.Samples[ i ] *= 60;
								}
							}
							else if( signalName.Equals( SignalNames.TidalVolume, StringComparison.Ordinal ) )
							{
								// I don't know why ResMed reports Tidal Volume in Liters, but ml is the standard
								signal.PhysicalDimension.Value =  "ml";
								signal.PhysicalMaximum.Value   *= 1000;
								signal.PhysicalMinimum.Value   *= 1000;
								
								for( int i = 0; i < signal.Samples.Count; i++ )
								{
									signal.Samples[ i ] *= 1000;
								}
							}

							// Not every signal within a Session will have the same start and end time as the others
							// because of differences in sampling rate, so we keep track of the start time and end
							// time of each Signal separately. Note the addition of a time adjustment, which allows
							// the user to calibrate for "drift" of the ResMed machine's internal clock.  
							var startTime  = header.StartTime.Value + _timeAdjustment;
							var endTime    = startTime.AddSeconds( header.NumberOfDataRecords * header.DurationOfDataRecord );

							// We need to see if the session already contains a signal by this name, so we know what to do with it. 
							if( session.GetSignalByName( signalName ) == null )
							{
                                // Add the signal to the current session
                                AddSignalToSession( session, startTime, endTime, signal );
							}
							else
							{
								// If a session already contains a signal by this name, it means that the ResMed machine has
								// given us MaskOn/MaskOff times that do not match the stored files. This happens when there
								// is a brief interruption in recording such as turning the machine off then back on again 
								// within a short enough time period that it doesn't start a new session, which ends up creating
								// a discontinuous session.
								//
								// We don't allow discontinuous sessions, so when this occurs the simplest thing to do is split
								// the session by creating another one with the same start and end times and adding the signal to
								// that session instead. We don't need to worry about the overlapping times because those will
								// be trimmed to match the stored signals below.
								
								// Check to see if there is an in-progress session that does not yet have this signal.
								var splitSession = day.Sessions.FirstOrDefault( x => session.StartTime <= fileDate && session.EndTime >= fileDate && x.GetSignalByName( signalName ) == null );
								
								// If there is, add it. 
								if( splitSession != null )
								{
                                    AddSignalToSession( splitSession, startTime, endTime, signal );
								}
								else
								{
									// Create a new Session based on the current session's time period, which will contain all of
									// the discontinuous signals (or at least the next set, as there may be more than one).
									var newSession = new Session()
									{
										StartTime = session.StartTime,
										EndTime   = session.EndTime,
									};
									
                                    AddSignalToSession( newSession, startTime, endTime, signal );
									
									// Add the session to the DailyReport. We'll need to sort sessions afterward because of this. 
									day.Sessions.Add( newSession );
								}
							}
						}
					}

					break;
				}
			}

			// Indicate whether the day has any detail data 
			day.HasDetailData = day.Sessions.Any( x => x.Signals.Count > 0 );
			
			foreach( var maskSession in day.Sessions )
			{
				// TODO: SpO2 and Pulse being invalid can actually be detected in the day's settings. Switch to that instead.
				// Remove all signals whose values are all out of range. This is how the AirSense indicates that 
				// there is no SpO2 or Pulse information when a pulse oximeter is not attached, and there may 
				// be other similar situations I haven't encountered yet (and in any case such signals are not valid). 
				maskSession.Signals.RemoveAll( x =>
					(x.Name == SignalNames.SpO2 || x.Name == SignalNames.Pulse) &&
					!x.Samples.Any( value => value >= x.MinValue )
				);
			}

			// Remove all sessions that are shorter than five minutes
			day.Sessions.RemoveAll( x => x.Duration.TotalMinutes < 5 );
			
			// If the day no longer has any sessions, stop processing it
			if( day.Sessions.Count == 0 )
			{
				return;
			}

			// Remove all events that do not occur within a Session's timeframe. It seems that my AirSense 10 Autoset
			// will sometimes flag a Hypopnea *after* the session completes, and I don't know if that's an artifact
			// from the machine or due to short sessions being removed, but in either case we don't want events that
			// don't correspond to an existing Session.
			day.Events.RemoveAll( evt => !day.Sessions.Any( sess => sess.StartTime <= evt.StartTime && sess.EndTime >= evt.StartTime ) );

			// If there is SpO2 and Pulse data, split those signals off into separate sessions for more logical grouping
			int sessionCount = day.Sessions.Count;
			for( int i = 0; i < sessionCount; i++ )
			{
				var session   = day.Sessions[ i ];
				var oxySignal = session.GetSignalByName( SignalNames.SpO2 );
				
				if( oxySignal != null )
				{
					var newSession = new Session
					{
						StartTime  = session.StartTime,
						EndTime    = session.EndTime,
						Source     = session.Source,
						SourceType = SourceType.PulseOximetry,
					};
					
					newSession.AddSignal( oxySignal );
					session.Signals.Remove( oxySignal );

					var pulseSignal = session.GetSignalByName( SignalNames.Pulse );
					if( pulseSignal != null )
					{
						newSession.AddSignal( pulseSignal );
						session.Signals.Remove( pulseSignal );
					}
					
					day.AddSession( newSession );
				}
			}
			
			// Sort the sessions by start time. This is only actually needed when we split a session above during
			// signal matching, but doesn't hurt anything when no sessions are split. 
			day.Sessions.Sort();
			
			var firstRecordedTime = DateTime.MaxValue;
			var lastRecordedTime  = DateTime.MinValue;
			
			// Now that each Session has all of its Signals added, each with correct StartTime and EndTime values, 
			// we can update the StartTime and EndTime of the Sessions. These values were previously set by the 
			// MaskOn/MaskOff values, which are convenience values used to match up session files and not accurate
			// start and end times. 
			if( day.HasDetailData )
			{
				foreach( var session in day.Sessions )
				{
					// Reset the session times to ensure that we don't keep artificial boundary times 
					session.StartTime = DateTime.MaxValue;
					session.EndTime   = DateTime.MinValue;

					// Session start and end times must bound all signal start and end times 
					foreach( var signal in session.Signals )
					{
						session.StartTime = DateUtil.Min( session.StartTime, signal.StartTime );
						session.EndTime   = DateUtil.Max( session.EndTime, signal.EndTime );
					}

					// Generate any additional signals that we need
					GenerateCalculatedSignals( day, session );

					lastRecordedTime  = DateUtil.Max( lastRecordedTime, session.EndTime );
					firstRecordedTime = DateUtil.Min( firstRecordedTime, session.StartTime );
				}
				
				// There is probably *some* value in keeping the "reported" start times and durations, but if so I cannot
				// think of what it is and doing so makes working with and displaying the data a lot more frustrating, so
				// we'll use the *actual* recorded session times instead. 
				day.RecordingStartTime = firstRecordedTime;
				day.RecordingEndTime   = lastRecordedTime;
				day.TotalSleepTime     = TimeSpan.FromSeconds( day.Sessions.Sum( x => x.Duration.TotalSeconds ) );

				// Calculate statistics (min, avg, median, max, etc) for each Signal
				CalculateSignalStatistics( day );
			
				// Generate events that are of interest which are not reported by the ResMed machine
				GenerateEvents( day );
			}
		}
		
		private static void GenerateCalculatedSignals( DailyReport day, Session session )
		{
			DerivedSignals.GenerateApneaIndexSignal( day, session );
			DerivedSignals.GenerateMissingRespirationSignals( day, session );
		}
		
		private static void GenerateEvents( DailyReport day )
		{
			GenerateFlowReductionEvents( day );
			GenerateLeakEvents( day );
			GenerateFlowLimitEvents( day );
		}

		private static void GenerateFlowReductionEvents( DailyReport day )
		{
			const double MINIMUM_EVENT_DURATION   = 8.0; // Only flag flow reductions that last this number of seconds or more
			const double FLOW_REDUCTION_THRESHOLD = 0.5; // Flag flow reductions of 50% or more
			const int    WINDOW_LENGTH            = 120; // Two minutes, per 2007 AASM Manual

			foreach( var session in day.Sessions )
			{
				var signal = session.GetSignalByName( SignalNames.FlowRate );
				if( signal == null )
				{
					return;
				}

				var absFlow      = signal.Samples.Select( Math.Abs ).ToArray();
				var filteredFlow = ButterworthFilter.Filter( absFlow, signal.FrequencyInHz, 1 );
				var calc         = new MovingAverageCalculator( (int)(WINDOW_LENGTH * signal.FrequencyInHz) );

				var interval   = 1.0 / signal.FrequencyInHz;
				var state      = 0;
				var startIndex = 0;
				var threshold  = 0.0;

				for( int i = 0; i < signal.Samples.Count; i++ )
				{
					var sample = filteredFlow[ i ];

					calc.AddObservation( sample );

					if( !calc.HasFullPeriod )
					{
						continue;
					}

					var rms = calc.Average + calc.StandardDeviation;

					switch( state )
					{
						case 0:
							threshold = rms * FLOW_REDUCTION_THRESHOLD;
							if( sample <= threshold )
							{
								startIndex = i;
								state      = 1;
							}
							break;

						case 1:
							if( sample >= threshold )
							{
								var duration = (i - startIndex) * interval;

								if( duration >= MINIMUM_EVENT_DURATION )
								{
									// Note that all machine-generated events "start" at the end of the event.
									// TODO: It might be better to rename ReportedEvent.StartTime to ReportedEvent.MarkerTime
									var eventStart = signal.StartTime.AddSeconds( i * interval );

									var newEvent = new ReportedEvent
									{
										Type      = EventType.FlowReduction,
										StartTime = eventStart,
										Duration  = TimeSpan.FromSeconds( duration ),
									};

									// Because this is an application-generated event, allow machine-generated events to take
									// precedence by not generating any events that overlap in time. 
									if( !day.Events.Any( x => ReportedEvent.TimesOverlap( x, newEvent ) ) )
									{
										day.Events.Add( newEvent );
									}
								}

								state = 0;
							}
							break;
					}
				}
			}
		}

		private static void GenerateFlowLimitEvents( DailyReport day )
		{
			// TODO: Flow Limitation event parameters need to be a configurable value 
			const double FlowLimitRedline = 0.3;
			const int    MinEventDuration = 3;

			List<ReportedEvent> flowLimitEvents = new List<ReportedEvent>();
			
			foreach( var session in day.Sessions )
			{
				var signal = session.GetSignalByName( SignalNames.FlowLimit );
				if( signal != null )
				{
					flowLimitEvents.AddRange( Annotate( EventType.FlowLimitation, signal, MinEventDuration, FlowLimitRedline, false ) );
				}
			}
			
			// HACK: Don't add any flow limitation events that coincide with a RERA event, since the two are related
			// and we don't want to double-count the events when calculating the RDI
			var eventTypeList = EventTypes.RespiratoryDisturbance;
			foreach( var flowLimit in flowLimitEvents )
			{
				if( !day.Events.Any( x => eventTypeList.Contains( x.Type ) && ReportedEvent.TimesOverlap( x, flowLimit ) ) )
				{
					day.Events.Add( flowLimit );
				}
			}
		}

		private static void GenerateLeakEvents( DailyReport day )
		{
			// TODO: Leak Redline needs to be a configurable value 
			const double LeakRedline = 24;
			
			foreach( var session in day.Sessions )
			{
				var signal = session.GetSignalByName( SignalNames.LeakRate );
				if( signal != null )
				{
					day.Events.AddRange( Annotate( EventType.LargeLeak, signal, 5, LeakRedline, false ) );
				}
			}
		}

		private static List<ReportedEvent> Annotate( EventType eventType, Signal signal, double minDuration, double redLine, bool interpolateSamples = true )
		{
			List<ReportedEvent> events = new List<ReportedEvent>();
			
			int    state      = 0;
			double eventStart = -1;

			var sourceData     = signal.Samples;
			var sampleInterval = 1.0 / signal.FrequencyInHz;
			
			for( int i = 1; i < sourceData.Count; i++ )
			{
				var sample = sourceData[ i ];
				var time   = i * sampleInterval;

				switch( state )
				{
					case 0:
					{
						if( sample > redLine )
						{
							var lastSample = sourceData[ i - 1 ];
							var t          = interpolateSamples ? MathUtil.InverseLerp( lastSample, sample, redLine ) : 1.0;

							eventStart = time - (1.0 - t) * sampleInterval;
							state      = 1;
						}
						
						break;
					}

					case 1:
					{
						// Find the specific time when the sample crossed the threshold, even if it 
						// doesn't align directly on a sample's interval
						var lastSample = sourceData[ i - 1 ];
						var t          = interpolateSamples ? MathUtil.InverseLerp( lastSample, sample, redLine ) : 1.0;
						var eventEnd   = time - (1.0 - t) * sampleInterval;
						var duration   = eventEnd - eventStart;

						if( sample <= redLine )
						{
							if( duration >= minDuration )
							{
								var annotation = new ReportedEvent
								{
									Type      = eventType,
									StartTime = signal.StartTime.AddSeconds( eventStart ),
									Duration  = TimeSpan.FromSeconds( duration ),
								};

								events.Add( annotation );
							}

							state = 0;
						}
						
						break;
					}
				}
			}

			if( state == 1 )
			{
				var annotation = new ReportedEvent()
				{
					Type      = eventType,
					StartTime = signal.StartTime.AddSeconds( eventStart ),
					Duration  = TimeSpan.FromSeconds( sourceData.Count * sampleInterval - eventStart ),
				};

				events.Add( annotation );
			}

			return events;
		}

		private static void CalculateSignalStatistics( DailyReport day )
		{
			// Determine the maximum sort buffer size we'll need so that we only need to allocate and reuse one buffer.
			var maxBufferSize = 0;

			// Note that when this was written, every session was guaranteed to have the same number of signals,
			// which appeared in the same order in each session. Since this is by design, that is not expected to
			// ever change, but it is worth noting here.
			for( int i = 0; i < day.Sessions[ 0 ].Signals.Count; i++ )
			{
				var signalSize = 0;
				foreach( var session in day.Sessions )
				{
					signalSize += session.Signals[ i ].Samples.Count;
				}

				maxBufferSize = Math.Max( maxBufferSize, signalSize );
			}

			foreach( var signal in day.Sessions[ 0 ].Signals )
			{
				// Automatically calculate statistics for all Signals whose value range is zero or above
				if( signal.MinValue >= 0 && signal.MaxValue > signal.MinValue )
				{
					// Skip the AHI signal, though
					if( signal.Name == SignalNames.AHI )
					{
						continue;
					}
					
					var calculator = new SignalStatCalculator();
					day.Statistics.Add( calculator.CalculateStats( signal.Name, day.Sessions ) );
				}
			}
		}

		private void LoadCheyneStokesEvents( string logFolder, DailyReport day )
		{
			var filenames = Directory.GetFiles( logFolder, "*_CSL.edf" );
			foreach( var filename in filenames )
			{
				var file = EdfFile.Open( filename );
				day.RecordingStartTime = file.Header.StartTime.Value + _timeAdjustment;
				
				foreach( var annotationSignal in file.AnnotationSignals )
				{
					double csrStartTime = double.MinValue;
					
					foreach( var annotation in annotationSignal.Annotations )
					{
						// Discard all timekeeping annotations, those aren't relevant to anything. 
						if( annotation.IsTimeKeepingAnnotation )
						{
							continue;
						}

						// Try to convert the annotation text into an Enum for easier processing. 
						var eventFlag = ReportedEvent.FromEdfAnnotation( day.RecordingStartTime, annotation );
						
						// We don't need the "Recording Starts" annotations either 
						if( eventFlag.Type == EventType.RecordingStarts )
						{
							continue;
						}

						if( annotation.Annotation.Equals( "CSR Start", StringComparison.OrdinalIgnoreCase ) )
						{
							csrStartTime = annotation.Onset;
						}
						else if( annotation.Annotation.Equals( "CSR End", StringComparison.OrdinalIgnoreCase ) )
						{
							// We're trusting the CPAP machine not to have overlapping CSR events
							Debug.Assert( csrStartTime >= 0, "CSR Start/End pair mismatch found" );
							
							var newEvent = new ReportedEvent
							{
								StartTime   = day.RecordingStartTime.AddSeconds( csrStartTime ),
								Duration    = TimeSpan.FromSeconds( annotation.Onset - csrStartTime ),
								Type        = EventType.CSR,
							};

							day.Events.Add( newEvent );

							// Resetting the csrStartTime to an invalid value allows us to detect when Start/End pairs are mismatched
							csrStartTime = double.MinValue;
						}
						else
						{
							Debug.Assert( false, $"Unhandled Event Type in {filename}: {annotation.Annotation}" );
						}
					}
				}
			}
		}
		
		private void LoadEventsAndAnnotations( string logFolder, DailyReport day )
		{
			var filenames = Directory.GetFiles( logFolder, "*_EVE.edf" );
			foreach( var filename in filenames )
			{
				var file = EdfFile.Open( filename );
				day.RecordingStartTime = file.Header.StartTime.Value + _timeAdjustment;
				
				foreach( var annotationSignal in file.AnnotationSignals )
				{
					foreach( var annotation in annotationSignal.Annotations )
					{
						// Discard all timekeeping annotations, those aren't relevant to anything. 
						if( annotation.IsTimeKeepingAnnotation )
						{
							continue;
						}

						// Try to convert the annotation text into an Enum for easier processing. 
						var eventFlag = ReportedEvent.FromEdfAnnotation( day.RecordingStartTime, annotation );
						
						// TODO: The ResMed AirSense 10 reports RERA as "Arousal"? Can RERA be correlated with Flow Limit to differentiate from non-effort-related Arousal? A cursory investigation suggests yes.
						
						// We don't need the "Recording Starts" annotations either 
						if( eventFlag.Type == EventType.RecordingStarts )
						{
							continue;
						}

						// Add the event flags to the current day
						day.Events.Add( eventFlag );
					}
				}
			}
			
			day.Events.Sort();
		}

		private List<DailyReport> LoadIndexAndSettings( string filename, DateTime minDate, DateTime maxDate )
		{
			var days = new List<DailyReport>();
			
			var file = EdfFile.Open( filename );

			if( file.Signals.Count == 0 )
			{
				return days;
			}

			// The STR.edf file is essentially a vertical table containing the settings data for each
			// recorded day. We need to transpose that data and use it to create a DailyReport for 
			// each available day. 
			for( int i = 0; i < file.Signals[ 0 ].Samples.Count; i++ )
			{
				// Gather a hash table of settings for a single day from across the signals 
				var lookup = new Dictionary<string, double>();
				for( int j = 0; j < file.Signals.Count; j++ )
				{
					var signalName = SignalNames.GetStandardName( file.Signals[ j ].Label );
					lookup[ signalName ] = file.Signals[ j ].Samples[ i ];
				}

				// Read in and process the settings for a single day
				var day = ReadDailyReport( lookup );
				day.MachineInfo        =  _machineInfo;
				day.RecordingStartTime += _timeAdjustment;

				days.Add( day );
			}

			// Mask On and Mask Off times are stored as the number of seconds since the day started.
			// Remember that according to ResMed, the day starts at 12pm (noon) instead of the more conventional 
			// and sane 12am (midnight).
			// There will be a maximum of ten MaskOn/MaskOff events per day (always true?)
			var maskOnSignal  = file.GetSignalByName( "MaskOn",  "Mask On" );
			var maskOffSignal = file.GetSignalByName( "MaskOff", "Mask Off" );

			// There will be an even number of MaskOn/MaskOff times for each day
			var numberOfEntriesPerDay = maskOnSignal.Samples.Count / days.Count;
			Debug.Assert( maskOnSignal.Samples.Count % numberOfEntriesPerDay == 0, "Invalid calculation of Number of Sessions Per Day" );

			for( int dayIndex = 0; dayIndex < days.Count; dayIndex++ )
			{
				var day = days[ dayIndex ];

				if( day.TotalSleepTime.TotalMinutes < 5 )
				{
					continue;
				}

				for( int i = 0; i < day.MaskEvents; i++ )
				{
					var sampleIndex = dayIndex * numberOfEntriesPerDay + i;

					// Stop processing MaskOn/MaskOff when we encounter a -1
					if( maskOnSignal.Samples[ sampleIndex ] < 0 || maskOffSignal.Samples[ sampleIndex ] < 0 )
					{
						break;
					}

					// Mask times are stored as the number of seconds since the "day" started. Remember that
					// the ResMed "day" starts at 12pm (noon) and continues until the next calendar day at 12pm.
					var maskOn   = day.ReportDate.AddMinutes( maskOnSignal.Samples[ sampleIndex ] );
					var maskOff  = day.ReportDate.AddMinutes( maskOffSignal.Samples[ sampleIndex ] );

					// Discard empty sessions
					if( maskOff.Subtract( maskOn ).TotalMinutes < 1 )
					{
						continue;
					}

					var session = new Session()
					{
						SourceType = SourceType.CPAP,
						StartTime  = maskOn,
						EndTime    = maskOff,
					};

					day.Sessions.Add( session );
				}

				day.RecordingStartTime = day.Sessions.Min( x => x.StartTime );
				day.RecordingEndTime   = day.Sessions.Max( x => x.EndTime );
			}
			
            // Remove all days that are too short to be valid or are otherwise invalid
            RemoveInvalidDays( days );
            
            // Remove days that don't match the provided range. It's less efficient to do this after we've already 
            // gathered the basic day information, but it keeps the code much cleaner and more readable, and this 
            // isn't exactly a performance-critical section of code ;)
            FilterDaysByDate( days, minDate, maxDate );

			return days;
		}

		/// <summary>
		/// Reads the statistics, settings, and other information from the stored data
		/// </summary>
		private DailyReport ReadDailyReport( Dictionary<string, double> data )
		{
			// TODO: Retain all raw settings data on import
			// I've tried my best to decode what all of the data means, and convert it to meaningful typed
			// values exposed in a reasonable manner, but it's highly likely that there's something I didn't
			// understand correctly, not to mention fields that are different for different models, so the
			// raw data should be kept available for the consumer of this library to make use of if needs be.

			var day = new DailyReport
			{
				MachineInfo    = _machineInfo,
				ReportDate     = new DateTime( 1970, 1, 1 ).AddDays( data[ "Date" ] ).AddHours( 12 ),
				Settings       = ReadMachineSettings( data ),
				EventSummary   = ReadEventsSummary( data ),
				StatsSummary   = ReadStatsSummary( data ),
				MaskEvents     = (int)(data[ "MaskEvents" ] / 2),
				TotalSleepTime = TimeSpan.FromMinutes( data[ "Duration" ] ),
				PatientHours   = getValue( "PatientHours" ),
				Fault =
				{
					Device     = getValue( "Fault.Device" ),
					Alarm      = getValue( "Fault.Alarm" ),
					Humidifier = getValue( "Fault.Humidifier" ),
					HeatedTube = getValue( "Fault.HeatedTube" ),
				},
			};

			return day;
			
			double getValue( string key )
			{
				return data.TryGetValue( key, out double value ) ? value : 0.0;
			}
		}

		private static EventSummary ReadEventsSummary( Dictionary<string, double> data )
		{
			Debug.Assert( data.ContainsKey( "AHI" ) );

			var summary = new EventSummary
			{
				AHI                      = getValue( "AHI" ),
				ApneaIndex               = getValue( "AI" ),
				HypopneaIndex            = getValue( "HI" ),
				ObstructiveApneaIndex    = getValue( "OAI" ),
				CentralApneaIndex        = getValue( "CAI" ),
				UnclassifiedApneaIndex   = getValue( "UAI" ),
				RespiratoryArousalIndex  = getValue( "RIN" ),
				CheynesStokesRespiration = getValue( "CSR" ),
			};

			return summary;

			// Because different models (AirSense 10 vs. AirCurve 10 for instance) provide different
			// summary data, we need to check each key to make sure it is available and return a
			// default value if it is not. 
			double getValue( string key )
			{
				return data.TryGetValue( key, out double value ) ? value : 0.0;
			}
		}

		private static StatisticsSummary ReadStatsSummary( Dictionary<string, double> data )
		{
			Debug.Assert( data.ContainsKey( "Leak.95" ) );

			var summary = new StatisticsSummary
			{
				Leak95     = getValue( "Leak.95" ),
				LeakMedian = getValue( "Leak.50" ),

				RespirationRateMax    = getValue( "RespRate.Max" ),
				RespirationRate95     = getValue( "RespRate.95" ),
				RespirationRateMedian = getValue( "RespRate.50" ),

				MinuteVentilationMax    = getValue( "MinVent.Max" ),
				MinuteVentilation95     = getValue( "MinVent.95" ),
				MinuteVentilationMedian = getValue( "MinVent.50" ),

				TidalVolumeMax    = getValue( "TidVol.Max" ) * 1000.0,
				TidalVolume95     = getValue( "TidVol.95" ) * 1000.0,
				TidalVolumeMedian = getValue( "TidVol.50" ) * 1000.0,

				PressureMax    = getValue( "MaskPress.Max" ),
				Pressure95     = getValue( "MaskPress.95" ),
				PressureMedian = getValue( "MaskPress.50" ),

				TargetIpapMax    = getValue( "TgtIPAP.50" ),
				TargetIpap95     = getValue( "TgtIPAP.50" ),
				TargetIpapMedian = getValue( "TgtIPAP.50" ),

				TargetEpapMax    = getValue( "TgtEPAP.50" ),
				TargetEpap95     = getValue( "TgtEPAP.50" ),
				TargetEpapMedian = getValue( "TgtEPAP.50" ),
			};

			return summary;
			
			// Because different models (AirSense 10 vs. AirCurve 10 for instance) provide different
			// summary data, we need to check each key to make sure it is available and return a
			// default value if it is not. 
			double getValue( string key )
			{
				return data.TryGetValue( key, out double value ) ? value : 0.0;
			}
		}

		private static MachineSettings ReadMachineSettings( Dictionary<string, double> data )
		{
			var settings = new MachineSettings();
			
			if( data.TryGetValue( "CPAP_MODE", out double legacyMode ) )
			{
				switch( (int)legacyMode )
				{
					case 1:
					case 2:
						settings.Mode = OperatingMode.APAP;
						break;
					case 3:
						settings.Mode = OperatingMode.CPAP;
						break;
					default:
						settings.Mode = OperatingMode.UNKNOWN;
						break;
				}
			}
			else
			{
				var mode = (int)data.GetValue( "Mode" );

				if( s_modeMapping.TryGetValue( mode, out OperatingMode mappedMode ) )
				{
					settings.Mode = mappedMode;
				}
				else
				{
					settings.Mode = (OperatingMode)mode;
				}
			}
			
			if(settings.Mode == OperatingMode.ASV )
			{
				settings.ASV = new AsvSettings
				{
					StartPressure = data[ "S.AV.StartPress" ],
					MinPressureSupport   = data[ "S.AV.MinPS" ],
					MaxPressureSupport   = data[ "S.AV.MaxPS" ],
					EPAP          = data[ "S.AV.EPAP" ],
					EpapMin       = data[ "S.AA.MinEPAP" ],
					EpapMax       = data[ "S.AA.MaxEPAP" ],
					IpapMin       = data[ "S.AV.EPAP" ] + data[ "S.AV.MinPS" ],
					IpapMax       = data[ "S.AV.EPAP" ] + data[ "S.AV.MaxPS" ],
				};
			}
			else if( settings.Mode == OperatingMode.ASV_VARIABLE_EPAP )
			{
				settings.ASV = new AsvSettings
				{
					StartPressure = data[ "S.AA.StartPress" ],
					MinPressureSupport   = data[ "S.AA.MinPS" ],
					MaxPressureSupport   = data[ "S.AA.MaxPS" ],
					EPAP          = data[ "S.AA.MinEPAP" ],
					EpapMin       = data[ "S.AA.MinEPAP" ],
					EpapMax       = data[ "S.AA.MaxEPAP" ],
				};
			}
			else if( settings.Mode == OperatingMode.AVAPS )
			{
				settings.Avap = new AvapSettings
				{
					StartPressure = data[ "S.i.StartPress" ],
					MinPressure   = data[ "S.i.MinPS" ],
					MaxPressure   = data[ "S.i.MaxPS" ],
					EpapAuto      = data[ "S.i.EPAPAuto" ] > 0.5,
					EPAP          = data[ "S.i.EPAP" ],
					EpapMin       = data[ "S.i.EPAP" ],
					EpapMax       = data[ "S.i.EPAP" ],
				};

				if( settings.Avap.EpapAuto )
				{
					settings.Avap.EpapMin = data[ "S.i.MinEPAP" ];
					settings.Avap.EpapMax = data[ "S.i.MaxEPAP" ];
					
					settings.Avap.IPAP    = settings.Avap.EpapMin + settings.Avap.MinPressure;
					settings.Avap.IpapMin = settings.Avap.EpapMin + settings.Avap.MinPressure;
					settings.Avap.IpapMax = settings.Avap.EpapMax + settings.Avap.MaxPressure;
				}
				else 
				{
					settings.Avap.IPAP    = settings. Avap.EPAP + settings.Avap.MinPressure;
					settings.Avap.IpapMin = settings.Avap.EPAP + settings.Avap.MinPressure;
					settings.Avap.IpapMax = settings.Avap.EPAP + settings.Avap.MaxPressure;
				}
			}

			settings.HeatedTube = data["HeatedTube"] > 0.5;
			settings.Humidifier = data["Humidifier"] > 0.5;

			settings.CPAP.StartPressure = data["S.C.StartPress"];
			settings.CPAP.Pressure      = data["S.C.Press"];

			if( data.TryGetValue( "S.EPR.EPREnable", out double eprEnabledValue ) )
			{
				settings.EPR.EprEnabled = eprEnabledValue >= 0.5;
				settings.EPR.Level      = (int)data[ "S.EPR.Level" ];
				settings.EPR.Mode       = (EprType)(int)(data[ "S.EPR.EPRType" ] + 1);

				settings.AutoSet.ResponseType  = (AutoSetResponseType)(int)data[ "S.AS.Comfort" ];
				settings.AutoSet.StartPressure = data[ "S.AS.StartPress" ];
				settings.AutoSet.MaxPressure   = data[ "S.AS.MaxPress" ];
				settings.AutoSet.MinPressure   = data[ "S.AS.MinPress" ];
			}

			settings.RampMode = (RampModeType)(int)data["S.RampEnable"];
			settings.RampTime = data["S.RampTime"];

			settings.SmartStart          = data["S.SmartStart"] > 0.5 ? OnOffType.On : OnOffType.Off;
			settings.AntibacterialFilter = data["S.ABFilter"] >= 0.5;

			settings.ClimateControl     = (ClimateControlType)(int)data["S.ClimateControl"];
			settings.Tube               = data["S.Tube"];
			settings.HumidifierStatus   = data["S.HumEnable"] > 0.5 ? OnOffType.On : OnOffType.Off;
			settings.HumidityLevel      = data["S.HumLevel"];
			settings.TemperatureEnabled = data["S.TempEnable"] > 0.5;
			settings.Temperature        = data["S.Temp"] * 1.8 + 32; // Converted from Celsius to Fahrenheit

			settings.Mask = (MaskType)(int)data["S.Mask"];

			settings.Essentials = data["S.PtAccess"] > 0.5 ? EssentialsMode.On : EssentialsMode.Plus;

			settings.PtAccess = data["S.PtAccess"];

			return settings;
		}

		private static void FilterDaysByDate( List<DailyReport> days, DateTime minDate, DateTime maxDate )
		{
			int dayIndex = 0;
			while( dayIndex < days.Count )
			{
				var date = days[ dayIndex ].ReportDate.Date;

				if( date < minDate )
				{
					days.RemoveAt( dayIndex );
					continue;
				}

				if( date > maxDate )
				{
					days.RemoveAt( dayIndex );
					continue;
				}

				dayIndex += 1;
			}
		}

		private static void RemoveInvalidDays( List<DailyReport> days )
		{
			int dayIndex = 0;
			while( dayIndex < days.Count )
			{
				if( days[ dayIndex ].TotalSleepTime.TotalMinutes <= 5 )
				{
					days.RemoveAt( dayIndex );
					continue;
				}

				dayIndex += 1;
			}
		}
		
		private static void AddSignalToSession( Session session, DateTime startTime, DateTime endTime, EdfStandardSignal fileSignal )
		{
			// Rename signals to their "standard" names. Among other things, this lets us standardize the 
			// data even when there might be slight differences in signal names among various machine models.
			var signalName = SignalNames.GetStandardName( fileSignal.Label.Value );

			Signal signal = session.GetSignalByName( signalName, StringComparison.Ordinal );

			if( signal != null )
			{
				throw new Exception( $"The session starting at {session.StartTime:g} already contains a Signal named '{signalName}'" );
			}
			
			signal = new Signal
			{
				Name              = signalName,
				StartTime         = startTime,
				EndTime           = endTime,
				FrequencyInHz     = fileSignal.FrequencyInHz,
				MinValue          = fileSignal.PhysicalMinimum,
				MaxValue          = fileSignal.PhysicalMaximum,
				UnitOfMeasurement = fileSignal.PhysicalDimension,
			};

			signal.Samples.AddRange( fileSignal.Samples );

			session.Signals.Add( signal );
		}
	}
}
