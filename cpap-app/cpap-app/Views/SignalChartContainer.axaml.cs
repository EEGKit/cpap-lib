﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

using cpap_app.Configuration;
using cpap_app.Controls;
using cpap_app.Events;
using cpap_app.ViewModels;

using cpaplib;

namespace cpap_app.Views;

public partial class SignalChartContainer : UserControl
{
	private List<ISignalGraph> _charts = new();

	private DispatcherTimer? _dragTimer     = null;
	private SignalChart?     _dragTarget    = null;
	private int              _dragDirection = 0;

	private DispatcherTimer? _renderTimer = null;

	#region Constructor 
	
	public SignalChartContainer()
	{
		InitializeComponent();

		List<SignalChartConfiguration> signalConfigs = SignalChartConfigurationStore.GetSignalConfigurations();
		List<EventMarkerConfiguration> eventConfigs  = EventMarkerConfigurationStore.GetEventMarkerConfigurations();

		AddHandler( SignalChart.ChartConfigurationChangedEvent, OnChartConfigurationChanged );
		AddHandler( SignalChart.ChartDraggedEvent,              Chart_Dragged );
		AddHandler( GraphEvents.TimeMarkerChangedEvent,         ChartOnTimeMarkerChanged );
		AddHandler( GraphEvents.DisplayedRangeChangedEvent,     ChartDisplayedRangeChanged );

		foreach( var config in signalConfigs )
		{
			if( !config.IsVisible )
			{
				continue;
			}

			var chart = new SignalChart() { ChartConfiguration = config, MarkerConfiguration = eventConfigs };

			if( !string.IsNullOrEmpty( config.SecondarySignalName ) )
			{
				var secondaryConfig = signalConfigs.FirstOrDefault( x => x.SignalName.Equals( config.SecondarySignalName, StringComparison.OrdinalIgnoreCase ) );
				if( secondaryConfig != null )
				{
					chart.SecondaryConfiguration = secondaryConfig;
				}
			}

			_charts.Add( chart );

			if( config.IsPinned )
			{
				PinnedCharts.Children.Add( chart );
			}
			else
			{
				UnPinnedCharts.Children.Add( chart );
			}
		}
	}
	
	#endregion
	
	#region Base class overrides 

	protected override void OnUnloaded( RoutedEventArgs e )
	{
		base.OnUnloaded( e );

		if( _dragTimer is { IsEnabled: true } )
		{
			_dragTimer.Stop();
		}

		if( _renderTimer is { IsEnabled: true } )
		{
			_renderTimer.Stop();
		}
	}
	
	#endregion 

	#region Event handlers

	private void Chart_Dragged( object? sender, SignalChart.ChartDragEventArgs e )
	{
		if( e.Source is not SignalChart chart || chart.ChartConfiguration == null )
		{
			return;
		}
		
		_dragTimer ??= new DispatcherTimer( TimeSpan.FromSeconds( 0.15 ), DispatcherPriority.Default, ( _, _ ) =>
		{
			if( _dragTarget != null )
			{
				DragChart( _dragTarget, _dragDirection );
				_dragTarget = null;
			}
		} );

		_dragTarget    = chart;
		_dragDirection = e.Direction;

		_dragTimer.Start();
	}

	private void DragChart( SignalChart chart, int direction )
	{
		var config       = chart.ChartConfiguration;
		var container    = config!.IsPinned ? PinnedCharts : UnPinnedCharts;
		var controlIndex = container.Children.IndexOf( chart );
		
		switch( direction )
		{
			case < 0 when controlIndex == 0:
				return;
			case > 0 when controlIndex == container.Children.Count - 1:
				return;
			case < 0:
			{
				var swap = container.Children[ controlIndex - 1 ] as SignalChart;
				Debug.Assert( swap != null );
				
				var updatedConfigs = SignalChartConfigurationStore.SwapDisplayOrder( chart.ChartConfiguration!, swap.ChartConfiguration! );
				UpdateConfigurations( updatedConfigs );
				
				container.Children.Move( controlIndex, controlIndex - 1 );
				
				break;
			}
			case > 0:
			{
				var swap = container.Children[ controlIndex + 1 ] as SignalChart;
				Debug.Assert( swap != null );

				var updatedConfigs = SignalChartConfigurationStore.SwapDisplayOrder( chart.ChartConfiguration!, swap.ChartConfiguration! );
				UpdateConfigurations( updatedConfigs );
				
				container.Children.Move( controlIndex, controlIndex + 1 );
				
				break;
			}
		}

		Dispatcher.UIThread.Post( chart.BringIntoView, DispatcherPriority.Default );
	}

	private void OnChartConfigurationChanged( object? sender, ChartConfigurationChangedEventArgs e )
	{
		if( e is { Source: SignalChart chart, PropertyName: nameof( SignalChartConfiguration.IsPinned ) } )
		{
			Chart_IsPinnedChanged( chart, e.ChartConfiguration );
		}
		
		var configurations = SignalChartConfigurationStore.Update( e.ChartConfiguration );
		
		UpdateConfigurations( configurations );
	}

	private void Chart_IsPinnedChanged( SignalChart chart, SignalChartConfiguration config )
	{
		if( config == null )
		{
			throw new Exception( $"Unexpected null value on property {nameof( SignalChartConfiguration )}" );
		}
		
		chart.SaveState();

		if( config.IsPinned )
		{
			config.DisplayOrder = 255;
			
			UnPinnedCharts.Children.Remove( chart );
			PinnedCharts.Children.Add( chart );
		}
		else
		{
			config.DisplayOrder = -1;
			
			PinnedCharts.Children.Remove( chart );
			UnPinnedCharts.Children.Insert( 0, chart );
		}
		
		chart.RestoreState();
	}

	private void ChartDisplayedRangeChanged( object? sender, DateTimeRangeRoutedEventArgs e )
	{
		foreach( var control in _charts.Where( control => control != e.Source ) )
		{
			control.SetDisplayedRange( e.StartTime, e.EndTime );
		}

		RenderAll( false );
		
		ResetRenderTimer();
	}

	private void ChartOnTimeMarkerChanged( object? sender, DateTimeRoutedEventArgs e )
	{
		foreach( var control in _charts )
		{
			if( control != sender )
			{
				control.UpdateTimeMarker( e.DateTime );
			}
		}
	}

	internal void SelectTimeRange( DateTime startTime, DateTime endTime )
	{
		if( DataContext is not DailyReport || _charts.Count == 0 )
		{
			return;
		}

		foreach( var control in _charts )
		{
			control.SetDisplayedRange( startTime, endTime );
		}

		RenderAll( false );
		
		ResetRenderTimer();
	}
	
	#endregion
	
	#region Public functions

	public void SelectSignal( string signalName )
	{
		Debug.Assert( !_charts.Any( x => x.ChartConfiguration == null ), $"At least one chart does not have a {nameof( SignalChart.ChartConfiguration )} value assigned." );
		
		foreach( var chart in _charts )
		{
			if( chart.ChartConfiguration!.SignalName == signalName || chart.ChartConfiguration!.Title == signalName )
			{
				((Control)chart).Focus();
				return;
			}
		}
	}

	public void ShowEventType( EventType eventType )
	{
		Debug.Assert( !_charts.Any( x => x.ChartConfiguration == null ), $"At least one chart does not have a {nameof( SignalChart.ChartConfiguration )} value assigned." );
		
		// First search to see if there's already a chart that's visible that displays the event type
		foreach( var chart in _charts )
		{
			if( chart.ChartConfiguration!.DisplayedEvents.Contains( eventType ) )
			{
				var chartControl = (Control)chart;
				if( chartControl.IsEffectivelyVisible )
				{
					chartControl.Focus();
					return;
				}
			}
		}
		
		// Search for a chart that is configured to show the event type, and scroll the first one found into view
		foreach( var chart in _charts )
		{
			if( chart.ChartConfiguration!.DisplayedEvents.Contains( eventType ) )
			{
				var chartControl = (Control)chart;
				
				if( !chart.ChartConfiguration.IsPinned )
				{
					chartControl.BringIntoView();
				}

				chartControl.Focus();

				return;
			}
		}
	}
	
	#endregion
	
	#region Private functions

	private void RenderAll( bool highQuality = false )
	{
		foreach( var control in _charts )
		{
			control.RenderGraph( highQuality );
		}
	}

	private void ResetRenderTimer()
	{
		_renderTimer ??= new DispatcherTimer( TimeSpan.FromSeconds( 0.25 ), DispatcherPriority.Default, ( _, _ ) =>
		{
			_renderTimer!.Stop();
			
			foreach( var control in _charts )
			{
				control.RenderGraph( true );
			}
		} );

		_renderTimer.Stop();
		_renderTimer.Start();
	}
	
	private void UpdateConfigurations( List<SignalChartConfiguration> configurations )
	{
		foreach( var chart in _charts )
		{
			Debug.Assert( chart.ChartConfiguration != null );
			
			chart.ChartConfiguration = configurations.First( x => x.ID == chart.ChartConfiguration.ID );
		}
	}

	#endregion 
}

