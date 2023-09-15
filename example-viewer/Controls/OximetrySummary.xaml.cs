﻿using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;

using cpaplib;

using example_viewer.Loaders;

using Microsoft.Win32;

namespace example_viewer.Controls;

public partial class OximetrySummary
{
	public event DailyReportModifiedHandler DailyReportModified;
	
	public OximetrySummary()
	{
		InitializeComponent();
	}
	
	private void BtnImportOxy_OnClick( object sender, RoutedEventArgs e )
	{
		if( DataContext is not DailyReport day )
		{
			MessageBox.Show( Application.Current.MainWindow, "There is no Daily Report to attach Pulse Oximeter data to" );
			return;
		}
		
		var loader = new OxyLinkLoader();
		
		var ofd = new OpenFileDialog
		{
			CheckPathExists  = true,
			DefaultExt       = loader.FileExtension,
			DereferenceLinks = true,
			Filter           = loader.FilenameFilter,
			//InitialDirectory = ApplicationPreferences.Instance.ImportFolder,
			Title            = $"Import from {loader.FriendlyName}",
			CheckFileExists  = true,
			Multiselect      = true,
		};

		var dialogResult = ofd.ShowDialog( Application.Current.MainWindow );
		if( (bool)!dialogResult )
		{
			return;
		}

		Session addedSession = null;

		foreach( var fullPath in ofd.FileNames )
		{
			if( !File.Exists( fullPath ) )
			{
				continue;
			}

			var filename = Path.GetFileName( fullPath );

			if( !string.IsNullOrEmpty( loader.FilenameMatchPattern ) )
			{
				if( !Regex.IsMatch( filename, loader.FilenameMatchPattern, RegexOptions.IgnoreCase ) )
				{
					Debug.WriteLine( $"File '{filename}' does not match the file naming convention for '{loader.FriendlyName}'" );
					continue;
				}
			}

			using( var file = File.OpenRead( fullPath ) )
			{
				(List<Session> sessions, _ ) = loader.Load( file );
				if( sessions == null || sessions.Count == 0 )
				{
					Debug.WriteLine( $"Failed to import {filename}" );
					continue;
				}

				foreach( var session in sessions )
				{
					// Ensure that the oximetry session overlaps the current day before integrating it. 
					if( day.RecordingStartTime > session.EndTime || day.RecordingEndTime < session.StartTime )
					{
						MessageBox.Show( Application.Current.MainWindow, $"The pulse oximeter data does not match the current date and cannot be imported at this time." );
						continue;
					}
					
					day.AddSession( session );

					addedSession = session;
				}
			}
		}

		if( addedSession != null )
		{
			// Update the statistics for each Signal added 
			foreach( var signal in addedSession.Signals )
			{
				day.UpdateSignalStatistics( signal.Name );
			}
			
			DailyReportModified?.Invoke( this, day );
		}
	}
	
	protected override void OnPropertyChanged( DependencyPropertyChangedEventArgs e )
	{
		base.OnPropertyChanged( e );
	
		if( e.Property.Name == nameof( DataContext ) )
		{
			if( DataContext is DailyReport day )
			{
				Debug.WriteLine( $"Data binding: {day}" );
			}
		}
	}
}
