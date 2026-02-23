using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DataRippleAIDesktop.Models;

namespace DataRippleAIDesktop.HelperControls.AudioSpectrumAnalyzer
{
    public partial class AudioSpectrumVisualizer : UserControl
    {
        private const int BAR_PADDING = 2;

        // Bar resources
        protected DisplayBar[] _bars;
        protected Pen _barPen;
        protected int _barPenWidth;
        protected Pen _barBgPen;

        // Central line and base line resources
        protected Pen _baseLinePen;
        protected double CentralLineY;

        public AudioSpectrumVisualizer() : this(Color.FromRgb(65, 105, 225))
        {
            // Default constructor calls parameterized constructor with default blue color
        }

        public AudioSpectrumVisualizer(Color waveformColor)
        {
            // WPF uses its own rendering system, so we don't need SetStyle or DoubleBuffered
            this.Background = Brushes.Transparent;  // Make sure the background is transparent for drawing
            this.SnapsToDevicePixels = true;  // Ensures smooth rendering

            // Initialize pen colors (WPF uses Brush objects) with custom color
            _barPenWidth = 1;
          

            _baseLinePen = new Pen(new SolidColorBrush(waveformColor), _barPenWidth);
            _baseLinePen.DashStyle = DashStyles.Dash;

            _barPen = new Pen(new SolidColorBrush(waveformColor), _barPenWidth+1);
            _barPen.EndLineCap = PenLineCap.Round;

            _barBgPen = new Pen(new SolidColorBrush(waveformColor), _barPenWidth+1);
            _barBgPen.EndLineCap = PenLineCap.Round;
        }

        /// <summary>
        /// Sets the waveform color for the visualizer
        /// </summary>
        /// <param name="waveformColor">The color to use for the waveform</param>
        public void SetWaveformColor(Color waveformColor)
        {
            _baseLinePen = new Pen(new SolidColorBrush(waveformColor), _barPenWidth);
            _baseLinePen.DashStyle = DashStyles.Dash;

            _barPen = new Pen(new SolidColorBrush(waveformColor), _barPenWidth+1);
            _barPen.EndLineCap = PenLineCap.Round;

            _barBgPen = new Pen(new SolidColorBrush(waveformColor), _barPenWidth+1);
            _barBgPen.EndLineCap = PenLineCap.Round;

            InvalidateVisual();
        }

        public void Set(byte[] data)
        {
            // Normalize data (optional step)
            byte[] normData = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                normData[i] = (byte)(data[i] / 2);

            // Transform data into bars
            _bars = Transform(normData);

            // Redraw the control after updating the bars
            InvalidateVisual();
        }

        public void ClearDisplay()
        {
          
            _bars = null;
            // Redraw the control after clearing the bars
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            // Calculate the central line position (middle of the control)
            CentralLineY = this.ActualHeight / 2;

            // Draw the central line (at the middle of the control)
            drawingContext.DrawLine(_baseLinePen, new Point(0, CentralLineY), new Point(this.ActualWidth - 1, CentralLineY));

            if (_bars != null)
            {
                // Draw each bar, both above and below the central line
                foreach (var bar in _bars)
                {
                    // Background bar (optional)
                    drawingContext.DrawLine(_barBgPen, bar.Start, bar.End);

                    // Foreground bar (main visual)
                    drawingContext.DrawLine(_barPen, bar.Start, bar.End);
                }
            }
        }

        public DisplayBar[] Transform(byte[] data)
        {
            int widthForBar = (int)(this.ActualWidth - 1);
            int heightForBar = (int)(this.ActualHeight / 2 - 1);

            byte max = 1;
            for (int i = 0; i < data.Length; i++)
            {
                if (max < data[i])
                    max = data[i];
            }

            // Scale ratio for height
            float heightRatio = heightForBar * 1f / max;

            // Calculate padding width for the bars
            float barPaddingWidth = (widthForBar - BAR_PADDING) * 1f / data.Length;

            // Initialize bars array
            DisplayBar[] bars = new DisplayBar[data.Length * 2];
            int firstSpace = BAR_PADDING + (int)(_barPenWidth / 2);

            // Loop to create the bars
            for (int i = 0; i < data.Length; i++)
            {
                int barILeft = (int)(firstSpace + barPaddingWidth * i);

                // Bar for the bottom half
                bars[i] = new DisplayBar
                {
                    Start = new Point(barILeft,Convert.ToInt16(CentralLineY)),
                    End = new Point(barILeft, CentralLineY + (int)(data[i] * heightRatio))  // Below central line
                };

                // Bar for the top half
                bars[i + data.Length] = new DisplayBar
                {
                    Start = new Point(barILeft, CentralLineY),
                    End = new Point(barILeft, CentralLineY - (int)(data[i] * heightRatio))  // Above central line
                };
            }

            return bars;
        }
    }
}
