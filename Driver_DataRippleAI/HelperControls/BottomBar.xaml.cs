using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace DataRippleAIDesktop.HelperControls
{
    /// <summary>
    /// Interaction logic for BottomBar.xaml
    /// </summary>
    public partial class BottomBar : UserControl
    {
        public event RoutedEventHandler LiveCallClicked;
        public event RoutedEventHandler HistoryClicked;
        public event RoutedEventHandler ConnectClicked;
        public event RoutedEventHandler DisconnectClicked;
        public event RoutedEventHandler LogoutClicked;

        public BottomBar()
        {
            InitializeComponent();
            SetupEventHandlers();
        }

        private void SetupEventHandlers()
        {
            btnLiveCall.Click += (s, e) => LiveCallClicked?.Invoke(this, e);
            btnHistory.Click += (s, e) => HistoryClicked?.Invoke(this, e);
            btnConnect.Click += (s, e) => ConnectClicked?.Invoke(this, e);
            btnDisconnect.Click += (s, e) => DisconnectClicked?.Invoke(this, e);
            btnLogout.Click += (s, e) => LogoutClicked?.Invoke(this, e);
        }

        public void SetConnectionState(bool isConnected)
        {
            btnConnect.IsEnabled = !isConnected;
            btnDisconnect.IsEnabled = isConnected;
        }
    }
}
