using System;
using System.Collections.ObjectModel;
using System.Windows;

namespace ChatServer
{
    public partial class MainWindow : Window
    {
        private TcpChatServer _server;
        private ObservableCollection<string> _users = new ObservableCollection<string>();

        public MainWindow()
        {
            InitializeComponent();
            UsersListBox.ItemsSource = _users;
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(PortTextBox.Text, out int port) || port < 1 || port > 65535)
            {
                MessageBox.Show("Введите корректный номер порта (1–65535)", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _server = new TcpChatServer();
                _server.OnLogMessage += OnLogMessage;
                _server.OnClientConnected += OnClientConnected;
                _server.OnClientDisconnected += OnClientDisconnected;
                _server.OnMessageReceived += OnMessageReceived;

                _server.Start(port);

                StartButton.IsEnabled = false;
                StopButton.IsEnabled = true;
                PortTextBox.IsEnabled = false;
                StatusLabel.Content = $"✔ Сервер запущен на порту {port}";
                StatusLabel.Foreground = System.Windows.Media.Brushes.DarkGreen;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка запуска сервера: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _server?.Stop();
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            PortTextBox.IsEnabled = true;
            StatusLabel.Content = "Сервер остановлен";
            StatusLabel.Foreground = System.Windows.Media.Brushes.DarkRed;
            _users.Clear();
            ClientCountLabel.Content = "0";
        }

        private void OnLogMessage(string message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                LogListBox.Items.Add(message);
                LogListBox.ScrollIntoView(LogListBox.Items[LogListBox.Items.Count - 1]);
            });
        }

        private void OnClientConnected(string nickname)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (!_users.Contains(nickname))
                    _users.Add(nickname);
                ClientCountLabel.Content = _server?.ClientCount.ToString() ?? "0";
            });
        }

        private void OnClientDisconnected(string nickname)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                _users.Remove(nickname);
                ClientCountLabel.Content = _server?.ClientCount.ToString() ?? "0";
            });
        }

        private void OnMessageReceived(string sender, string text)
        {
            // Already logged in ChatServer.cs
        }

        private void CensorButton_Click(object sender, RoutedEventArgs e)
        {
            if (_server == null) return;
            bool on = CensorButton.IsChecked == true;
            _server.CensorEnabled = on;
            CensorButton.Content    = on ? "🛡 Цензура: ВКЛ" : "🛡 Цензура: ВЫКЛ";
            CensorButton.Background = on
                ? System.Windows.Media.Brushes.MediumPurple
                : System.Windows.Media.Brushes.Gray;
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _server?.Stop();
        }
    }
}
