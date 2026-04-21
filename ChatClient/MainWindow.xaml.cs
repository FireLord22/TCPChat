using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ChatClient
{
    // ── Модель элемента в списке чатов ──────────────────────────────────────────
    public class ChatEntry
    {
        public string Key     { get; }   // nick для ЛС, "group:N" для групп
        public string Display { get; set; }
        public bool   IsGroup { get; }
        public string GroupId { get; }
        public bool   HasUnread { get; set; }

        public ChatEntry(string key, string display, bool isGroup = false, string groupId = null)
        { Key = key; Display = display; IsGroup = isGroup; GroupId = groupId; }

        public override string ToString()
            => HasUnread ? $"● {Display}" : Display;
    }

    public partial class MainWindow : Window
    {
        private TcpChatClient _client;

        // Список чатов в левой панели
        private readonly ObservableCollection<ChatEntry> _chatEntries = new ObservableCollection<ChatEntry>();

        // Онлайн-пользователи (все с сервера)
        private readonly List<string> _onlineUsers = new List<string>();

        // chatKey → список сообщений (хранятся как UIElement-блоки)
        private readonly Dictionary<string, List<MessageBlock>> _history = new Dictionary<string, List<MessageBlock>>();

        // Активный чат
        private string _activeKey = null;

        // Таймер для плашки цензуры
        private DispatcherTimer _censorTimer;

        // groupId → groupName (группы где состоим)
        private readonly Dictionary<string, string> _myGroups = new Dictionary<string, string>();

        public MainWindow()
        {
            InitializeComponent();
            ChatListBox.ItemsSource = _chatEntries;
        }

        // ── Подключение / авторизация ─────────────────────────────────────────────

        private void PasswordBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) TryAuth(login: true);
        }

        private void LoginButton_Click(object sender, RoutedEventArgs e)    => TryAuth(login: true);
        private void RegisterButton_Click(object sender, RoutedEventArgs e) => TryAuth(login: false);

        private void TryAuth(bool login)
        {
            string host = HostTextBox.Text.Trim();
            string nick = NicknameTextBox.Text.Trim();
            string pass = PasswordBox.Password;

            if (string.IsNullOrWhiteSpace(host)) { ShowAuthError("Введите адрес сервера"); return; }
            if (!int.TryParse(PortTextBox.Text, out int port) || port < 1 || port > 65535)
            { ShowAuthError("Некорректный порт"); return; }
            if (string.IsNullOrWhiteSpace(nick)) { ShowAuthError("Введите логин"); return; }
            if (string.IsNullOrWhiteSpace(pass)) { ShowAuthError("Введите пароль"); return; }

            SetAuthUIEnabled(false);
            try
            {
                var client = new TcpChatClient();
                string connErr = client.Connect(host, port);
                if (!string.IsNullOrEmpty(connErr))
                { ShowAuthError(connErr); SetAuthUIEnabled(true); return; }

                string authErr = login ? client.Login(nick, pass) : client.Register(nick, pass);
                if (!string.IsNullOrEmpty(authErr))
                { client.Disconnect(); ShowAuthError(authErr); SetAuthUIEnabled(true); return; }

                _client = client;
                _client.OnSystemMessage      += OnSystemMessage;
                _client.OnUsersUpdated       += OnUsersUpdated;
                _client.OnDisconnected       += OnDisconnected;
                _client.OnPrivateMessage     += OnPrivateMessage;
                _client.OnPrivateMessageSent += OnPrivateMessageSent;
                _client.OnGroupCreated       += OnGroupCreated;
                _client.OnInviteReceived     += OnInviteReceived;
                _client.OnInviteAccepted     += OnInviteAccepted;
                _client.OnGroupMessage       += OnGroupMessage;
                _client.OnHistoryPm          += OnHistoryPm;
                _client.OnHistoryGroup       += OnHistoryGroup;
                _client.OnCensored           += OnCensored;

                SetConnected(true);
                Title = $"Гойдаграм — {_client.Nickname}";
                HideAuthError();
            }
            catch (Exception ex)
            { ShowAuthError($"Ошибка: {ex.Message}"); SetAuthUIEnabled(true); }
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            _client?.Disconnect();
            SetConnected(false);
        }

        private void ShowAuthError(string msg)
        { AuthErrorLabel.Text = msg; AuthErrorLabel.Visibility = Visibility.Visible; }

        private void HideAuthError()
        { AuthErrorLabel.Visibility = Visibility.Collapsed; }

        private void SetAuthUIEnabled(bool enabled)
        {
            HostTextBox.IsEnabled     = enabled;
            PortTextBox.IsEnabled     = enabled;
            NicknameTextBox.IsEnabled = enabled;
            PasswordBox.IsEnabled     = enabled;
            LoginButton.IsEnabled     = enabled;
            RegisterButton.IsEnabled  = enabled;
        }

        // ── Отправка ────────────────────────────────────────────────────────────

        private void SendButton_Click(object sender, RoutedEventArgs e) => TrySend();
        private void MessageTextBox_KeyDown(object sender, KeyEventArgs e)
        { if (e.Key == Key.Enter) TrySend(); }

        private void TrySend()
        {
            string text = MessageTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(text) || _activeKey == null) return;

            if (_activeKey.StartsWith("group:"))
                _client?.SendGroupMsg(_activeKey.Substring(6), text);
            else
                _client?.SendPm(_activeKey, text);
            // Своё сообщение придёт через PM_SENT / GROUPMSG — не дублируем

            MessageTextBox.Clear();
            MessageTextBox.Focus();
        }

        // ── Левая панель ────────────────────────────────────────────────────────

        private void ChatListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var entry = ChatListBox.SelectedItem as ChatEntry;
            if (entry == null) return;

            entry.HasUnread = false;
            // Сброс отображения "●" — обновляем через замену объекта
            int idx = _chatEntries.IndexOf(entry);
            _chatEntries[idx] = entry; // триггерит обновление ToString()

            SwitchToChat(entry.Key);
        }

        private void NewGroupButton_Click(object sender, RoutedEventArgs e)
        {
            string name = Microsoft.VisualBasic.Interaction.InputBox(
                "Название группы:", "Создать группу", "");
            if (!string.IsNullOrWhiteSpace(name))
                _client?.CreateGroup(name);
        }

        private void InviteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeKey == null || !_activeKey.StartsWith("group:")) return;
            string gid = _activeKey.Substring(6);

            // Показываем список онлайн-пользователей не в группе
            // Для простоты — InputBox с никнеймом
            string nick = Microsoft.VisualBasic.Interaction.InputBox(
                "Введите никнейм пользователя для приглашения:", "Пригласить", "");
            if (!string.IsNullOrWhiteSpace(nick))
                _client?.InviteToGroup(gid, nick);
        }

        // ── Переключение активного чата ─────────────────────────────────────────

        private void SwitchToChat(string key)
        {
            _activeKey = key;
            MessagesPanel.Children.Clear();

            bool isGroup = key.StartsWith("group:");

            // Заголовок
            if (isGroup)
            {
                string gid = key.Substring(6);
                ChatTitleLabel.Text    = _myGroups.TryGetValue(gid, out var gn) ? gn : "Группа";
                ChatSubtitleLabel.Text = "групповой чат";
                InviteButton.Visibility = Visibility.Visible;
                InviteButton.IsEnabled  = true;
            }
            else
            {
                ChatTitleLabel.Text    = key;
                ChatSubtitleLabel.Text = _onlineUsers.Contains(key) ? "в сети" : "не в сети";
                InviteButton.Visibility = Visibility.Collapsed;
            }

            // Показываем историю
            PlaceholderText.Visibility = Visibility.Collapsed;
            MessagesScroll.Visibility  = Visibility.Visible;

            if (_history.TryGetValue(key, out var msgs))
                foreach (var mb in msgs)
                    MessagesPanel.Children.Add(mb.Build());

            ScrollToBottom();
            MessageTextBox.IsEnabled = true;
            SendButton.IsEnabled     = true;
            MessageTextBox.Focus();
        }

        // ── Добавление сообщения ────────────────────────────────────────────────



        private void AddMessage(string chatKey, string sender, string text, MsgKind kind)
        {
            if (!_history.ContainsKey(chatKey))
                _history[chatKey] = new List<MessageBlock>();

            var mb = new MessageBlock(sender, text, kind, DateTime.Now);
            _history[chatKey].Add(mb);

            if (_activeKey == chatKey)
            {
                MessagesPanel.Children.Add(mb.Build());
                ScrollToBottom();
            }
            else
            {
                // Пометить непрочитанным
                var entry = FindEntry(chatKey);
                if (entry != null && !entry.HasUnread)
                {
                    entry.HasUnread = true;
                    int idx = _chatEntries.IndexOf(entry);
                    if (idx >= 0) _chatEntries[idx] = entry;
                }
            }
        }

        // ── Обработчики событий TcpChatClient ──────────────────────────────────

        private void OnSystemMessage(string text)
        {
            Dispatch(() =>
            {
                // Системные — в активный чат, или игнорируем если нет активного
                if (_activeKey != null)
                    AddMessage(_activeKey, "★", text, MsgKind.System);
            });
        }

        private void OnUsersUpdated(string[] users)
        {
            Dispatch(() =>
            {
                _onlineUsers.Clear();
                foreach (var u in users)
                    if (!string.IsNullOrWhiteSpace(u) && u != _client?.Nickname)
                    {
                        _onlineUsers.Add(u);
                        EnsureChatEntry(u, u, false);
                    }
                // Обновить подпись если открыт ЛС
                if (_activeKey != null && !_activeKey.StartsWith("group:"))
                    ChatSubtitleLabel.Text = _onlineUsers.Contains(_activeKey) ? "в сети" : "не в сети";
            });
        }

        private void OnDisconnected()
        {
            Dispatch(() => SetConnected(false));
        }

        private void OnPrivateMessage(string from, string text)
        {
            Dispatch(() =>
            {
                EnsureChatEntry(from, from, false);
                AddMessage(from, from, text, MsgKind.Incoming);
            });
        }

        private void OnPrivateMessageSent(string to, string text)
        {
            Dispatch(() =>
            {
                EnsureChatEntry(to, to, false);
                AddMessage(to, "Вы", text, MsgKind.Outgoing);
            });
        }

        private void OnGroupCreated(string gid, string name)
        {
            Dispatch(() =>
            {
                string key = "group:" + gid;
                _myGroups[gid] = name;
                EnsureChatEntry(key, name, true, gid);
                // Автоматически открываем только что созданную группу
                SelectChatEntry(key);
            });
        }

        private void OnInviteReceived(string gid, string name, string from)
        {
            Dispatch(() =>
            {
                var res = MessageBox.Show(
                    $"{from} приглашает вас в группу «{name}».\n\nПринять?",
                    "Приглашение", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.Yes) _client?.AcceptInvite(gid);
                else                             _client?.DeclineInvite(gid);
            });
        }

        private void OnInviteAccepted(string gid, string name, string nick, string[] members)
        {
            Dispatch(() =>
            {
                string key = "group:" + gid;
                _myGroups[gid] = name;
                EnsureChatEntry(key, name, true, gid);
                string who = nick == _client?.Nickname ? "Вы" : nick;
                AddMessage(key, "★", $"{who} вступил(а). Участники: {string.Join(", ", members)}", MsgKind.System);
                if (nick == _client?.Nickname) SelectChatEntry(key);
            });
        }

        private void OnGroupMessage(string gid, string sender, string text)
        {
            Dispatch(() =>
            {
                string key  = "group:" + gid;
                bool   mine = sender == _client?.Nickname;
                AddMessage(key, mine ? "Вы" : sender, text, mine ? MsgKind.Outgoing : MsgKind.Incoming);
            });
        }

        private void OnCensored()
        {
            Dispatch(() => ShowCensorBanner());
        }

        private void ShowCensorBanner()
        {
            CensorBanner.Visibility = Visibility.Visible;

            // Сбрасываем предыдущий таймер если есть
            _censorTimer?.Stop();
            _censorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _censorTimer.Tick += (s, e) =>
            {
                CensorBanner.Visibility = Visibility.Collapsed;
                _censorTimer.Stop();
            };
            _censorTimer.Start();
        }

        private void OnHistoryPm(string peer, string sender, string text)
        {
            Dispatch(() =>
            {
                EnsureChatEntry(peer, peer, false);
                bool mine = sender == _client?.Nickname;
                // В историю добавляем тихо (без "непрочитанного")
                if (!_history.ContainsKey(peer)) _history[peer] = new List<MessageBlock>();
                _history[peer].Add(new MessageBlock(mine ? "Вы" : sender, text,
                    mine ? MsgKind.Outgoing : MsgKind.Incoming, DateTime.MinValue));
            });
        }

        private void OnHistoryGroup(string gid, string sender, string text)
        {
            Dispatch(() =>
            {
                string key  = "group:" + gid;
                bool   mine = sender == _client?.Nickname;
                if (!_history.ContainsKey(key)) _history[key] = new List<MessageBlock>();
                _history[key].Add(new MessageBlock(mine ? "Вы" : sender, text,
                    mine ? MsgKind.Outgoing : MsgKind.Incoming, DateTime.MinValue));
            });
        }

        // ── Вспомогательные ────────────────────────────────────────────────────

        private void EnsureChatEntry(string key, string display, bool isGroup, string gid = null)
        {
            foreach (var e in _chatEntries)
                if (e.Key == key) return;
            _chatEntries.Add(new ChatEntry(key, display, isGroup, gid));
        }

        private void SelectChatEntry(string key)
        {
            for (int i = 0; i < _chatEntries.Count; i++)
                if (_chatEntries[i].Key == key)
                {
                    ChatListBox.SelectedIndex = i;
                    return;
                }
        }

        private ChatEntry FindEntry(string key)
        {
            foreach (var e in _chatEntries)
                if (e.Key == key) return e;
            return null;
        }

        private void ScrollToBottom()
        {
            MessagesScroll.UpdateLayout();
            MessagesScroll.ScrollToBottom();
        }

        private void SetConnected(bool on)
        {
            DisconnectButton.IsEnabled = on;
            NewGroupButton.IsEnabled   = on;
            SendButton.IsEnabled       = false;
            MessageTextBox.IsEnabled   = false;
            InviteButton.IsEnabled     = false;

            AuthPanel.Visibility     = on ? Visibility.Collapsed : Visibility.Visible;
            LoggedInPanel.Visibility = on ? Visibility.Visible   : Visibility.Collapsed;
            if (on) LoggedInLabel.Text = $"Вошли как  {_client?.Nickname}";

            if (!on)
            {
                _chatEntries.Clear();
                _history.Clear();
                _myGroups.Clear();
                _onlineUsers.Clear();
                _activeKey = null;
                MessagesPanel.Children.Clear();
                ChatTitleLabel.Text    = "Выберите чат";
                ChatSubtitleLabel.Text = "";
                PlaceholderText.Visibility = Visibility.Visible;
                MessagesScroll.Visibility  = Visibility.Collapsed;
                InviteButton.Visibility    = Visibility.Collapsed;
                PasswordBox.Clear();
                SetAuthUIEnabled(true);
                Title = "Гойдаграм";
            }
        }

        private static void Warn(string msg)
            => MessageBox.Show(msg, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);

        private static void Dispatch(Action a)
            => Application.Current.Dispatcher.Invoke(a);

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
            => _client?.Disconnect();
    }

    // ── Блок сообщения ─────────────────────────────────────────────────────────
    public enum MsgKind { Incoming, Outgoing, System }

    public class MessageBlock
    {
        private readonly string  _sender;
        private readonly string  _text;
        private readonly MsgKind _kind;
        private readonly DateTime _time;

        public MessageBlock(string sender, string text, MsgKind kind, DateTime time)
        { _sender = sender; _text = text; _kind = kind; _time = time; }

        public UIElement Build()
        {
            bool hasTime = _time != DateTime.MinValue;

            // Цвета
            Color bg, fg;
            HorizontalAlignment align;

            switch (_kind)
            {
                case MsgKind.Outgoing:
                    bg    = Color.FromRgb(74, 158, 255);   // синий
                    fg    = Colors.White;
                    align = HorizontalAlignment.Right;
                    break;
                case MsgKind.System:
                    bg    = Color.FromRgb(58, 58, 60);
                    fg    = Color.FromRgb(142, 142, 147);
                    align = HorizontalAlignment.Center;
                    break;
                default: // Incoming
                    bg    = Color.FromRgb(44, 44, 46);
                    fg    = Color.FromRgb(242, 242, 247);
                    align = HorizontalAlignment.Left;
                    break;
            }

            var bubble = new Border
            {
                Background       = new SolidColorBrush(bg),
                CornerRadius     = new CornerRadius(12),
                Padding          = new Thickness(12, 8, 12, 8),
                Margin           = new Thickness(0, 3, 0, 3),
                MaxWidth         = 480,
                HorizontalAlignment = align
            };

            var inner = new StackPanel { Orientation = Orientation.Vertical };

            // Имя отправителя (только для входящих не-системных)
            if (_kind == MsgKind.Incoming)
            {
                inner.Children.Add(new TextBlock
                {
                    Text       = _sender,
                    Foreground = new SolidColorBrush(Color.FromRgb(142, 142, 147)),
                    FontSize   = 11,
                    Margin     = new Thickness(0, 0, 0, 2)
                });
            }

            // Текст
            inner.Children.Add(new TextBlock
            {
                Text         = _text,
                Foreground   = new SolidColorBrush(fg),
                FontSize     = 13,
                TextWrapping = TextWrapping.Wrap
            });

            // Время
            if (hasTime)
            {
                inner.Children.Add(new TextBlock
                {
                    Text       = _time.ToString("HH:mm"),
                    Foreground = new SolidColorBrush(
                        _kind == MsgKind.Outgoing
                            ? Color.FromArgb(160, 255, 255, 255)
                            : Color.FromRgb(100, 100, 105)),
                    FontSize   = 10,
                    Margin     = new Thickness(0, 3, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right
                });
            }

            bubble.Child = inner;

            // Обёртка для выравнивания
            var wrapper = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            wrapper.Children.Add(bubble);
            return wrapper;
        }
    }
}
