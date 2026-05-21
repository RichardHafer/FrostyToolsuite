using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

// Source - https://stackoverflow.com/a/36061935
// Posted by Siderite Zackwehdex, modified by community. See post 'Timeline' for change history
// Retrieved 2026-03-02, License - CC BY-SA 3.0

// huge thanks to them! idk why outlined text isnt a thing in the first place, but they made this so much easier!

namespace RimeWidgetBlueprintEditor.Editor.Text
{
    public class StrokeAdorner : Adorner
    {
        private TextBlock _textBlock;

        private Brush _stroke;
        private ushort _strokeThickness;
        private Brush _fill;

        public Brush Stroke
        {
            get
            {
                return _stroke;
            }

            set
            {
                _stroke = value;
                _textBlock.InvalidateVisual();
                InvalidateVisual();
            }
        }

        public ushort StrokeThickness
        {
            get
            {
                return _strokeThickness;
            }

            set
            {
                _strokeThickness = value;
                _textBlock.InvalidateVisual();
                InvalidateVisual();
            }
        }

        public Brush Fill
        {
            get
            {
                return _fill;
            }

            set
            {
                _fill = value;
                _textBlock.InvalidateVisual();
                InvalidateVisual();
            }
        }

        public StrokeAdorner(UIElement adornedElement) : base(adornedElement)
        {
            _textBlock = adornedElement as TextBlock;
            ensureTextBlock();
            foreach (var property in TypeDescriptor.GetProperties(_textBlock).OfType<PropertyDescriptor>())
            {
                var dp = DependencyPropertyDescriptor.FromProperty(property);
                if (dp == null) continue;
                var metadata = dp.Metadata as FrameworkPropertyMetadata;
                if (metadata == null) continue;
                if (!metadata.AffectsRender) continue;
                dp.AddValueChanged(_textBlock, (s, e) => this.InvalidateVisual());
            }
        }

        private void ensureTextBlock()
        {
            if (_textBlock == null) throw new Exception("This adorner works on TextBlocks only");
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            ensureTextBlock();
            base.OnRender(drawingContext);
            var formattedText = new FormattedText(
                _textBlock.Text,
                CultureInfo.CurrentUICulture,
                _textBlock.FlowDirection,
                new Typeface(_textBlock.FontFamily, _textBlock.FontStyle, _textBlock.FontWeight, _textBlock.FontStretch),
                _textBlock.FontSize,
                 Brushes.Black // This brush does not matter since we use the geometry of the text. 
            );

            formattedText.TextAlignment = _textBlock.TextAlignment;
            formattedText.Trimming = _textBlock.TextTrimming;
            formattedText.LineHeight = _textBlock.LineHeight;
            formattedText.MaxTextWidth = _textBlock.ActualWidth - _textBlock.Padding.Left - _textBlock.Padding.Right;
            formattedText.MaxTextHeight = _textBlock.ActualHeight - _textBlock.Padding.Top;// - _textBlock.Padding.Bottom;
            while (formattedText.Extent == double.NegativeInfinity)
            {
                formattedText.MaxTextHeight++;
            }

            // Build the geometry object that represents the text.
            var _textGeometry = formattedText.BuildGeometry(new Point(_textBlock.Padding.Left, _textBlock.Padding.Top));

            // (UI Blueprint Editor) slightly edited this part so that the stroke is a lot less sharp
            var textPen = new Pen(Stroke, StrokeThickness)
            {
                DashCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
            };

            drawingContext.DrawGeometry(_fill, textPen, _textGeometry);
        }

    }


    public class StrokeTextBlock : TextBlock
    {
        private StrokeAdorner _adorner;
        private bool _adorned = false;

        public StrokeTextBlock()
        {
            _adorner = new StrokeAdorner(this);
            this.LayoutUpdated += StrokeTextBlock_LayoutUpdated;
        }

        private void StrokeTextBlock_LayoutUpdated(object sender, EventArgs e)
        {
            if (_adorned) return;
            _adorned = true;
            var adornerLayer = AdornerLayer.GetAdornerLayer(this);
            adornerLayer.Add(_adorner);
            this.LayoutUpdated -= StrokeTextBlock_LayoutUpdated;
        }

        private static void strokeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var stb = (StrokeTextBlock)d;
            stb._adorner.Stroke = e.NewValue as Brush;
        }

        private static void strokeThicknessChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var stb = (StrokeTextBlock)d;
            stb._adorner.StrokeThickness = DependencyProperty.UnsetValue.Equals(e.NewValue) ? (ushort)0 : (ushort)e.NewValue;
        }

        private static void fillChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var stb = (StrokeTextBlock)d;
            stb._adorner.Fill = e.NewValue as Brush;
        }

        /// <summary>
        /// Specifies the brush to use for the stroke and optional hightlight of the formatted text.
        /// </summary>
        public Brush Stroke
        {
            get
            {
                return (Brush)GetValue(StrokeProperty);
            }

            set
            {
                SetValue(StrokeProperty, value);
            }
        }

        /// <summary>
        /// Identifies the Stroke dependency property.
        /// </summary>
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            "Stroke",
            typeof(Brush),
            typeof(StrokeTextBlock),
            new FrameworkPropertyMetadata(
                 new SolidColorBrush(Colors.Teal),
                 FrameworkPropertyMetadataOptions.AffectsRender,
                 new PropertyChangedCallback(strokeChanged),
                 null
                 )
            );

        /// <summary>
        ///     The stroke thickness of the font.
        /// </summary>
        public ushort StrokeThickness
        {
            get
            {
                return (ushort)GetValue(StrokeThicknessProperty);
            }

            set
            {
                SetValue(StrokeThicknessProperty, value);
            }
        }

        /// <summary>
        /// Identifies the StrokeThickness dependency property.
        /// </summary>
        public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
            "StrokeThickness",
            typeof(ushort),
            typeof(StrokeTextBlock),
            new FrameworkPropertyMetadata(
                 (ushort)0,
                 FrameworkPropertyMetadataOptions.AffectsRender,
                 new PropertyChangedCallback(strokeThicknessChanged),
                 null
                 )
            );

        /// <summary>
        /// The color that fills the text.
        /// </summary>
        public Brush Fill
        {
            get
            {
                return (Brush)GetValue(FillProperty);
            }

            set
            {
                SetValue(FillProperty, value);
            }
        }

        /// <summary>
        /// Identifies the Fill dependency property.
        /// </summary>
        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
            "Fill",
            typeof(Brush),
            typeof(StrokeTextBlock),
            new FrameworkPropertyMetadata(
                 new SolidColorBrush(Colors.White),
                 FrameworkPropertyMetadataOptions.AffectsRender,
                 new PropertyChangedCallback(fillChanged),
                 null
                 )
            );
    }
}