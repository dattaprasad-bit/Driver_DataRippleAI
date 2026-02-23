using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DataRippleAIDesktop.HelperControls
{
    public partial class RoundedButtonCustomized : UserControl
    {
        public RoundedButtonCustomized()
        {
            InitializeComponent();
        }

        // Dependency property for button text
        public static readonly DependencyProperty ButtonTextProperty =
            DependencyProperty.Register("ButtonText", typeof(string), typeof(RoundedButtonCustomized),
                new PropertyMetadata("Default Text", OnButtonTextChanged));

        public string ButtonText
        {
            get => (string)GetValue(ButtonTextProperty);
            set => SetValue(ButtonTextProperty, value);
        }

        private static void OnButtonTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (RoundedButtonCustomized)d;
            var newText = e.NewValue?.ToString();
            control.ButtonTextBlock.Text = newText;
        }

        // Dependency property for command
        public static readonly DependencyProperty CommandProperty =
            DependencyProperty.Register("Command", typeof(ICommand), typeof(RoundedButtonCustomized),
                new PropertyMetadata(null));

        public ICommand Command
        {
            get => (ICommand)GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        public static readonly DependencyProperty VisibilityCommandProperty =
         DependencyProperty.Register("VisibilityCommand", typeof(Visibility), typeof(RoundedButtonCustomized),
             new PropertyMetadata(Visibility.Visible, OnButtonVisibilityChanged));

        public Visibility VisibilityCommand
        {
            get => (Visibility)GetValue(VisibilityCommandProperty);
            set => SetValue(VisibilityCommandProperty, value);
        }

        private static void OnButtonVisibilityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RoundedButtonCustomized roundedButtonDataRippleAI)
            {
               roundedButtonDataRippleAI.Visibility = (Visibility)e.NewValue;
            }
        }


        // Routed event for handling button clicks
        public static readonly RoutedEvent ButtonClickEvent =
            EventManager.RegisterRoutedEvent("ButtonClick", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(RoundedButtonCustomized));

        public event RoutedEventHandler ButtonClick
        {
            add => AddHandler(ButtonClickEvent, value);
            remove => RemoveHandler(ButtonClickEvent, value);
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            RaiseEvent(new RoutedEventArgs(ButtonClickEvent));
        }
    }
}
