using System;
using System.Windows;
using System.Windows.Controls;

namespace DataRippleAIDesktop.HelperControls
{
    /// <summary>
    /// Interaction logic for LoaderProgress.xaml
    /// </summary>
    public partial class LoaderProgress : UserControl
    {
        public LoaderProgress()
        {
            InitializeComponent();
        }

        // Dependency property for Visibility
        public static readonly DependencyProperty LoaderVisibilityProperty =
            DependencyProperty.Register("LoaderVisibility", typeof(Visibility), typeof(LoaderProgress),
                new PropertyMetadata(Visibility.Visible, OnLoaderVisibilityChanged));

        public Visibility LoaderVisibility
        {
            get => (Visibility)GetValue(LoaderVisibilityProperty);
            set => SetValue(LoaderVisibilityProperty, value);
        }

        private static void OnLoaderVisibilityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LoaderProgress loaderProgress)
            {
                loaderProgress.Loader.Visibility = (Visibility)e.NewValue;
            }
        }


        // Helper methods to show and hide the loader
        public void Show()
        {
            LoaderVisibility = Visibility.Visible;
        }

        public void Hide()
        {
            LoaderVisibility = Visibility.Collapsed;
        }
    }
}
