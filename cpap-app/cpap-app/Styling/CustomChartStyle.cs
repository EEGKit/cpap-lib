﻿using Avalonia.Controls;
using Avalonia.Media;

using cpap_app.Helpers;

using Color = System.Drawing.Color;

namespace cpap_app.Styling;

#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
#pragma warning disable CS8602 // Dereference of a possibly null reference.

public class CustomChartStyle : ScottPlot.Styles.Default
{
	#region Public properties 
	
	public override Color FrameColor            { get; }
	public override Color AxisLabelColor        { get; }
	public override Color DataBackgroundColor   { get; }
	public override Color FigureBackgroundColor { get; }
	public override Color GridLineColor         { get; }
	public override Color TickLabelColor        { get; }
	public override Color TickMajorColor        { get; }
	public override Color TickMinorColor        { get; }
	public override Color TitleFontColor        { get; }
		
	public override string TickLabelFontName { get; }
	public override string AxisLabelFontName { get; }
	public override string TitleFontName     { get; }
	
	#endregion 
	
	#region Constructor 
	
	public CustomChartStyle( IResourceHost control, Brush foreground, Brush background, Brush borderColor, Brush gridLineColor )
	{
		var foreColor       = ((SolidColorBrush)foreground).Color.ToDrawingColor();
		var border          = ((SolidColorBrush)borderColor).Color.ToDrawingColor();
		var midColor        = ((SolidColorBrush)gridLineColor).Color.ToDrawingColor().MultiplyAlpha( 0.5f );
		var backgroundColor = ((SolidColorBrush)background).Color.ToDrawingColor();
		var fontName        = FontManager.Current.DefaultFontFamily.Name;

		FigureBackgroundColor = Color.Transparent;
		DataBackgroundColor   = backgroundColor;
			
		FrameColor     = border;
		AxisLabelColor = foreColor;
		TitleFontColor = foreColor;
		TickLabelColor = foreColor;

		GridLineColor  = midColor;
		TickMajorColor = midColor;
		TickMinorColor = midColor;

		TickLabelFontName = fontName;
		AxisLabelFontName = fontName;
		TitleFontName     = fontName;
	}
	
	#endregion 
}