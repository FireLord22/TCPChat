using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ChatServer
{
    // ── Модель группы ───────────────────────────────────────────────────────────
    public class ChatGroup
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Creator { get; set; }
        public List<string> Members { get; set; } = new List<string>();
        public List<string> History { get; set; } = new List<string>();
        public ChatGroup() { }
        public ChatGroup(string id, string name, string creator)
        { Id = id; Name = name; Creator = creator; }
    }

    // ── Модель аккаунта ─────────────────────────────────────────────────────────
    public class Account
    {
        public string PasswordHash { get; set; }
        public string CreatedAt { get; set; }
    }

    // ── Сервер ──────────────────────────────────────────────────────────────────
    public class TcpChatServer
    {
        // ── Сетевое состояние
        private TcpListener _listener;
        private readonly Dictionary<string, StreamWriter> _clients = new Dictionary<string, StreamWriter>();
        private readonly object _lock = new object();
        private bool _isRunning;
        private int _nextGroupId = 1;

        // ── Персистентные данные
        private Dictionary<string, Account> _accounts = new Dictionary<string, Account>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, ChatGroup> _groups = new Dictionary<string, ChatGroup>();
        private Dictionary<string, List<string>> _pmHistory = new Dictionary<string, List<string>>();

        // ── Пути к файлам данных
        private string _dataDir;
        private string AccountsFile => Path.Combine(_dataDir, "accounts.json");
        private string GroupsFile => Path.Combine(_dataDir, "groups.json");
        private string PmHistoryDir => Path.Combine(_dataDir, "pm");
        private string GroupHistDir => Path.Combine(_dataDir, "grouphist");

        // ── События для UI
        public event Action<string> OnLogMessage;
        public event Action<string> OnClientConnected;
        public event Action<string> OnClientDisconnected;
        public event Action<string, string> OnMessageReceived;

        public int Port { get; private set; }
        public int ClientCount { get { lock (_lock) return _clients.Count; } }
        public bool CensorEnabled { get; set; } = true;

        // ── Хэш пароля ─────────────────────────────────────────────────────────
        private static string HashPassword(string password)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(password));
                return BitConverter.ToString(bytes).Replace("-", "").ToLower();
            }
        }

        // ── Фильтр мата ────────────────────────────────────────────────────────
        private static readonly string[] BadRoots =
        {
            "бля","блядь","блядин","ёб","еб",
            "хуй","хуя","хуе","хуйн","пизд",
            "пидор","пидар","мудак","мудил","сук[аи]",
            "залуп","манд","шлюх",
            "fuck","shit","bitch","cunt","dick","cock","pussy","asshole"
        };
        private static readonly string WC = @"[а-яёА-ЯЁa-zA-Z]*";

        private struct FR { public string Text; public bool Censored; }

        private FR Filter(string t)
        {
            bool hit = false;
            if (CensorEnabled)
                foreach (var r in BadRoots)
                    t = Regex.Replace(t, WC + r + WC, m => { hit = true; return "Гойда!"; },
                        RegexOptions.IgnoreCase);
            return new FR { Text = t, Censored = hit };
        }

        private static string PmKey(string a, string b)
        {
            var p = new[] { a, b }; Array.Sort(p, StringComparer.OrdinalIgnoreCase);
            return p[0].ToLower() + "_" + p[1].ToLower();
        }

        // ══ ЗАПУСК / ОСТАНОВКА ═════════════════════════════════════════════════

        public void Start(int port = 5000, string dataDir = "data")
        {
            Port = port;
            _dataDir = dataDir;
            Directory.CreateDirectory(_dataDir);
            Directory.CreateDirectory(PmHistoryDir);
            Directory.CreateDirectory(GroupHistDir);
            LoadAll();

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _isRunning = true;
            Log($"Гойдаграм-сервер запущен на порту {port}. Аккаунтов: {_accounts.Count}");
            new Thread(AcceptLoop) { IsBackground = true }.Start();
        }

        public void Stop()
        {
            _isRunning = false;
            _listener?.Stop();
            lock (_lock)
            {
                foreach (var w in _clients.Values) try { w.Close(); } catch { }
                _clients.Clear();
            }
            Log("Сервер остановлен");
        }

        // ══ ПЕРСИСТЕНТНОСТЬ ════════════════════════════════════════════════════

        private void LoadAll()
        {
            // Аккаунты
            if (File.Exists(AccountsFile))
            {
                try { _accounts = SimpleJson.DeserializeAccounts(File.ReadAllText(AccountsFile)); }
                catch { Log("Ошибка чтения accounts.json"); }
            }

            // Группы
            if (File.Exists(GroupsFile))
            {
                try
                {
                    var groups = SimpleJson.DeserializeGroups(File.ReadAllText(GroupsFile));
                    foreach (var g in groups)
                    {
                        _groups[g.Id] = g;
                        if (int.TryParse(g.Id, out int gid) && gid >= _nextGroupId)
                            _nextGroupId = gid + 1;
                    }
                }
                catch { Log("Ошибка чтения groups.json"); }
            }

            // История групп
            foreach (var g in _groups.Values)
            {
                string path = Path.Combine(GroupHistDir, $"group_{g.Id}.txt");
                if (File.Exists(path))
                    try { g.History = new List<string>(File.ReadAllLines(path)); }
                    catch { }
            }

            // История ЛС — загружаем все файлы из pm/
            foreach (var file in Directory.GetFiles(PmHistoryDir, "*.txt"))
            {
                string key = Path.GetFileNameWithoutExtension(file);
                try { _pmHistory[key] = new List<string>(File.ReadAllLines(file)); }
                catch { }
            }
        }

        private void SaveAccounts()
        {
            try { File.WriteAllText(AccountsFile, SimpleJson.SerializeAccounts(_accounts)); }
            catch (Exception ex) { Log($"Ошибка сохранения аккаунтов: {ex.Message}"); }
        }

        private void SaveGroups()
        {
            try { File.WriteAllText(GroupsFile, SimpleJson.SerializeGroups(_groups.Values)); }
            catch (Exception ex) { Log($"Ошибка сохранения групп: {ex.Message}"); }
        }

        private void AppendPmHistory(string key, string record)
        {
            try { File.AppendAllText(Path.Combine(PmHistoryDir, key + ".txt"), record + "\n"); }
            catch { }
        }

        private void AppendGroupHistory(string gid, string record)
        {
            try { File.AppendAllText(Path.Combine(GroupHistDir, $"group_{gid}.txt"), record + "\n"); }
            catch { }
        }

        // ══ ПРИЁМ СОЕДИНЕНИЙ ═══════════════════════════════════════════════════

        private void AcceptLoop()
        {
            while (_isRunning)
            {
                try
                {
                    var tcp = _listener.AcceptTcpClient();
                    new Thread(() => ClientLoop(tcp)) { IsBackground = true }.Start();
                }
                catch (SocketException) { if (_isRunning) Log("Ошибка приёма"); }
                catch (Exception ex) { if (_isRunning) Log($"Ошибка: {ex.Message}"); }
            }
        }

        // ══ ОБРАБОТКА КЛИЕНТА ══════════════════════════════════════════════════

        private void ClientLoop(TcpClient tcp)
        {
            string nick = null;
            StreamReader rd = null;
            StreamWriter wr = null;
            try
            {
                var ns = tcp.GetStream();
                rd = new StreamReader(ns);
                wr = new StreamWriter(ns) { AutoFlush = true };

                // ── Аутентификация / регистрация ───────────────────────────────
                nick = Authenticate(rd, wr);
                if (nick == null) return;   // не прошёл — соединение закрываем

                // ── Регистрируем как активного клиента ─────────────────────────
                lock (_lock)
                {
                    if (_clients.ContainsKey(nick))
                    { wr.WriteLine("SYSTEM:Этот аккаунт уже подключён"); return; }
                    _clients[nick] = wr;
                }

                Log($"+ {nick}");
                OnClientConnected?.Invoke(nick);
                wr.WriteLine($"SYSTEM:Добро пожаловать в Гойдаграм, {nick}!");
                BroadcastUserList();
                SendInitialData(nick, wr);

                // ── Основной цикл ──────────────────────────────────────────────
                string line;
                while ((line = rd.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("/pm ")) HandlePm(nick, wr, line);
                    else if (line.StartsWith("/creategroup ")) HandleCreateGroup(nick, wr, line);
                    else if (line.StartsWith("/invite ")) HandleInvite(nick, wr, line);
                    else if (line.StartsWith("/acceptinvite ")) HandleAcceptInvite(nick, line);
                    else if (line.StartsWith("/declineinvite ")) HandleDeclineInvite(nick, line);
                    else if (line.StartsWith("/groupmsg ")) HandleGroupMsg(nick, line);
                    else if (line.StartsWith("/users")) SendUserList(wr);
                    else if (line.StartsWith("/quit")) break;
                }
            }
            catch (IOException) { }
            catch (Exception ex) { Log($"Ошибка [{nick ?? "?"}]: {ex.Message}"); }
            finally
            {
                if (nick != null)
                {
                    lock (_lock) { _clients.Remove(nick); }
                    Log($"- {nick}");
                    OnClientDisconnected?.Invoke(nick);
                    BroadcastUserList();
                }
                try { rd?.Close(); } catch { }
                try { wr?.Close(); } catch { }
                try { tcp?.Close(); } catch { }
            }
        }

        // ── Аутентификация ──────────────────────────────────────────────────────
        // Протокол: клиент шлёт "/register nick pass" или "/login nick pass"
        // Сервер отвечает "AUTH_OK" или "AUTH_FAIL:причина"
        private string Authenticate(StreamReader rd, StreamWriter wr)
        {
            wr.WriteLine("AUTH_READY:");   // сигнал клиенту что можно слать /login или /register

            for (int attempt = 0; attempt < 5; attempt++)
            {
                string line = rd.ReadLine();
                if (line == null) return null;

                if (line.StartsWith("/register "))
                {
                    var p = line.Substring("/register ".Length).Split(new[] { ' ' }, 2);
                    if (p.Length < 2 || string.IsNullOrWhiteSpace(p[0]) || string.IsNullOrWhiteSpace(p[1]))
                    { wr.WriteLine("AUTH_FAIL:Неверный формат"); continue; }

                    string regNick = p[0].Trim();
                    string regPass = p[1].Trim();

                    if (regNick.Length < 2 || regNick.Length > 20)
                    { wr.WriteLine("AUTH_FAIL:Никнейм: 2–20 символов"); continue; }
                    if (regPass.Length < 4)
                    { wr.WriteLine("AUTH_FAIL:Пароль: минимум 4 символа"); continue; }

                    lock (_lock)
                    {
                        if (_accounts.ContainsKey(regNick))
                        { wr.WriteLine("AUTH_FAIL:Логин уже занят"); continue; }
                        _accounts[regNick] = new Account
                        {
                            PasswordHash = HashPassword(regPass),
                            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                        };
                        SaveAccounts();
                    }
                    Log($"Зарегистрирован: {regNick}");
                    wr.WriteLine($"AUTH_OK:{regNick}");
                    return regNick;
                }
                else if (line.StartsWith("/login "))
                {
                    var p = line.Substring("/login ".Length).Split(new[] { ' ' }, 2);
                    if (p.Length < 2)
                    { wr.WriteLine("AUTH_FAIL:Неверный формат"); continue; }

                    string loginNick = p[0].Trim();
                    string loginPass = p[1].Trim();

                    lock (_lock)
                    {
                        if (!_accounts.TryGetValue(loginNick, out var acc))
                        { wr.WriteLine("AUTH_FAIL:Аккаунт не найден"); continue; }
                        if (acc.PasswordHash != HashPassword(loginPass))
                        { wr.WriteLine("AUTH_FAIL:Неверный пароль"); continue; }
                    }
                    wr.WriteLine($"AUTH_OK:{loginNick}");
                    return loginNick;
                }
                else
                {
                    wr.WriteLine("AUTH_FAIL:Ожидается /login или /register");
                }
            }
            return null;
        }

        // ── Начальные данные ────────────────────────────────────────────────────
        private void SendInitialData(string nick, StreamWriter wr)
        {
            lock (_lock)
            {
                foreach (var g in _groups.Values)
                {
                    if (!g.Members.Contains(nick)) continue;
                    try { wr.WriteLine($"GROUP_CREATED:{g.Id}:{g.Name}"); } catch { }
                    foreach (var h in g.History)
                        try { wr.WriteLine($"HISTORY_GROUP:{g.Id}:{h}"); } catch { }
                }
                foreach (var kv in _pmHistory)
                {
                    var parts = kv.Key.Split('_');
                    if (parts.Length < 2) continue;
                    if (!parts[0].Equals(nick, StringComparison.OrdinalIgnoreCase) &&
                        !parts[1].Equals(nick, StringComparison.OrdinalIgnoreCase)) continue;
                    string peer = parts[0].Equals(nick, StringComparison.OrdinalIgnoreCase) ? parts[1] : parts[0];
                    foreach (var msg in kv.Value)
                        try { wr.WriteLine($"HISTORY_PM:{peer}:{msg}"); } catch { }
                }
            }
        }

        // ── Личные сообщения ────────────────────────────────────────────────────
        private void HandlePm(string sender, StreamWriter senderWr, string line)
        {
            var p = line.Substring(4).Split(new[] { ' ' }, 2);
            if (p.Length < 2) return;
            string target = p[0].Trim();
            var fr = Filter(p[1].Trim());
            string text = fr.Text;
            string record = $"{sender}:{text}";
            string key = PmKey(sender, target);

            lock (_lock)
            {
                if (!_pmHistory.ContainsKey(key)) _pmHistory[key] = new List<string>();
                _pmHistory[key].Add(record);

                if (_clients.TryGetValue(target, out var tw))
                    try { tw.WriteLine($"PM:{sender}:{text}"); } catch { }
                try { senderWr.WriteLine($"PM_SENT:{target}:{text}"); } catch { }
                if (fr.Censored)
                    try { senderWr.WriteLine("CENSORED:"); } catch { }
            }
            AppendPmHistory(key, record);
        }

        // ── Создание группы ─────────────────────────────────────────────────────
        private void HandleCreateGroup(string creator, StreamWriter wr, string line)
        {
            string name = line.Substring("/creategroup ".Length).Trim();
            if (string.IsNullOrWhiteSpace(name)) return;
            string gid;
            lock (_lock)
            {
                gid = (_nextGroupId++).ToString();
                var g = new ChatGroup(gid, name, creator);
                g.Members.Add(creator);
                _groups[gid] = g;
                SaveGroups();
            }
            Log($"Группа «{name}» id={gid}");
            try { wr.WriteLine($"GROUP_CREATED:{gid}:{name}"); } catch { }
        }

        // ── Инвайт ──────────────────────────────────────────────────────────────
        private void HandleInvite(string sender, StreamWriter wr, string line)
        {
            var p = line.Substring("/invite ".Length).Split(new[] { ' ' }, 2);
            if (p.Length < 2) return;
            string gid = p[0].Trim(); string nick = p[1].Trim();
            lock (_lock)
            {
                if (!_groups.TryGetValue(gid, out var g))
                { try { wr.WriteLine("SYSTEM:Группа не найдена"); } catch { } return; }
                if (g.Creator != sender)
                { try { wr.WriteLine("SYSTEM:Только создатель может приглашать"); } catch { } return; }
                if (!_clients.TryGetValue(nick, out var tw))
                { try { wr.WriteLine($"SYSTEM:{nick} не в сети"); } catch { } return; }
                if (g.Members.Contains(nick))
                { try { wr.WriteLine($"SYSTEM:{nick} уже в группе"); } catch { } return; }
                try { tw.WriteLine($"INVITE:{gid}:{g.Name}:{sender}"); } catch { }
                try { wr.WriteLine($"SYSTEM:Запрос отправлен → {nick}"); } catch { }
            }
        }

        // ── Принять инвайт ──────────────────────────────────────────────────────
        private void HandleAcceptInvite(string nick, string line)
        {
            string gid = line.Substring("/acceptinvite ".Length).Trim();
            lock (_lock)
            {
                if (!_groups.TryGetValue(gid, out var g) || g.Members.Contains(nick)) return;
                g.Members.Add(nick);
                SaveGroups();
                string members = string.Join(",", g.Members);
                foreach (var m in g.Members)
                    if (_clients.TryGetValue(m, out var w))
                        try { w.WriteLine($"INVITE_ACCEPTED:{gid}:{g.Name}:{nick}:{members}"); } catch { }
                if (_clients.TryGetValue(nick, out var nw))
                    foreach (var h in g.History)
                        try { nw.WriteLine($"HISTORY_GROUP:{gid}:{h}"); } catch { }
            }
        }

        // ── Отклонить инвайт ────────────────────────────────────────────────────
        private void HandleDeclineInvite(string nick, string line)
        {
            string gid = line.Substring("/declineinvite ".Length).Trim();
            lock (_lock)
            {
                if (!_groups.TryGetValue(gid, out var g)) return;
                if (_clients.TryGetValue(g.Creator, out var cw))
                    try { cw.WriteLine($"SYSTEM:{nick} отклонил приглашение в «{g.Name}»"); } catch { }
            }
        }

        // ── Сообщение в группу ──────────────────────────────────────────────────
        private void HandleGroupMsg(string sender, string line)
        {
            var p = line.Substring("/groupmsg ".Length).Split(new[] { ' ' }, 2);
            if (p.Length < 2) return;
            string gid = p[0].Trim();
            var fr = Filter(p[1].Trim());
            string text = fr.Text;
            string record = $"{sender}:{text}";
            lock (_lock)
            {
                if (!_groups.TryGetValue(gid, out var g) || !g.Members.Contains(sender)) return;
                g.History.Add(record);
                foreach (var m in g.Members)
                    if (_clients.TryGetValue(m, out var w))
                        try { w.WriteLine($"GROUPMSG:{gid}:{sender}:{text}"); } catch { }
                if (fr.Censored && _clients.TryGetValue(sender, out var sw))
                    try { sw.WriteLine("CENSORED:"); } catch { }
            }
            AppendGroupHistory(gid, record);
            OnMessageReceived?.Invoke(sender, text);
        }

        // ── Утилиты ─────────────────────────────────────────────────────────────
        private void SendUserList(StreamWriter wr)
        {
            lock (_lock)
            { try { wr.WriteLine($"USERS:{string.Join(",", _clients.Keys)}"); } catch { } }
        }

        private void BroadcastUserList()
        {
            lock (_lock)
            {
                string u = string.Join(",", _clients.Keys);
                foreach (var w in _clients.Values)
                    try { w.WriteLine($"USERS:{u}"); } catch { }
            }
        }

        private void Log(string msg) => OnLogMessage?.Invoke($"[{DateTime.Now:HH:mm:ss}] {msg}");
    }

    // ══ Простой JSON-сериализатор (без внешних зависимостей) ═══════════════════
    public static class SimpleJson
    {
        public static string SerializeAccounts(Dictionary<string, Account> accounts)
        {
            var sb = new StringBuilder("{\n");
            bool first = true;
            foreach (var kv in accounts)
            {
                if (!first) sb.Append(",\n");
                sb.Append($"  \"{Esc(kv.Key)}\": {{\"passwordHash\":\"{Esc(kv.Value.PasswordHash)}\",\"createdAt\":\"{Esc(kv.Value.CreatedAt)}\"}}");
                first = false;
            }
            sb.Append("\n}");
            return sb.ToString();
        }

        public static Dictionary<string, Account> DeserializeAccounts(string json)
        {
            var result = new Dictionary<string, Account>(StringComparer.OrdinalIgnoreCase);
            // Простой парсер: ищем пары "nick": {"passwordHash":"...","createdAt":"..."}
            var re = new Regex("\"([^\"]+)\"\\s*:\\s*\\{\\s*\"passwordHash\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"createdAt\"\\s*:\\s*\"([^\"]*)\"");
            foreach (Match m in re.Matches(json))
                result[m.Groups[1].Value] = new Account
                { PasswordHash = m.Groups[2].Value, CreatedAt = m.Groups[3].Value };
            return result;
        }

        public static string SerializeGroups(IEnumerable<ChatGroup> groups)
        {
            var sb = new StringBuilder("[\n");
            bool first = true;
            foreach (var g in groups)
            {
                if (!first) sb.Append(",\n");
                string members = string.Join(",", g.Members);
                sb.Append($"  {{\"id\":\"{Esc(g.Id)}\",\"name\":\"{Esc(g.Name)}\",\"creator\":\"{Esc(g.Creator)}\",\"members\":\"{Esc(members)}\"}}");
                first = false;
            }
            sb.Append("\n]");
            return sb.ToString();
        }

        public static List<ChatGroup> DeserializeGroups(string json)
        {
            var result = new List<ChatGroup>();
            var re = new Regex("\\{\\s*\"id\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"name\"\\s*:\\s*\"([^\"]*)\"\\s*,\\s*\"creator\"\\s*:\\s*\"([^\"]*)\"\\s*,\\s*\"members\"\\s*:\\s*\"([^\"]*)\"");
            foreach (Match m in re.Matches(json))
            {
                var g = new ChatGroup(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
                string mem = m.Groups[4].Value;
                if (!string.IsNullOrEmpty(mem))
                    g.Members.AddRange(mem.Split(','));
                result.Add(g);
            }
            return result;
        }

        private static string Esc(string s) => s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
    }
}
