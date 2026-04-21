using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace ChatClient
{
    public class TcpChatClient
    {
        private TcpClient    _tcp;
        private StreamReader _rd;
        private StreamWriter _wr;
        private bool         _connected;

        public string Nickname    { get; private set; }
        public bool   IsConnected => _connected;

        // ── События ─────────────────────────────────────────────────────────────
        public event Action<string>                           OnSystemMessage;
        public event Action<string[]>                         OnUsersUpdated;
        public event Action                                   OnDisconnected;
        public event Action<string, string>                   OnPrivateMessage;
        public event Action<string, string>                   OnPrivateMessageSent;
        public event Action<string, string>                   OnGroupCreated;
        public event Action<string, string, string>           OnInviteReceived;
        public event Action<string, string, string, string[]> OnInviteAccepted;
        public event Action<string, string, string>           OnGroupMessage;
        public event Action<string, string, string>           OnHistoryPm;
        public event Action<string, string, string>           OnHistoryGroup;
        public event Action                                   OnCensored;

        // ── Подключение (без авторизации) ────────────────────────────────────────
        // Возвращает пустую строку при успехе, иначе текст ошибки
        public string Connect(string host, int port)
        {
            _tcp = new TcpClient();
            _tcp.Connect(host, port);
            var ns = _tcp.GetStream();
            _rd = new StreamReader(ns);
            _wr = new StreamWriter(ns) { AutoFlush = true };

            // Ждём AUTH_READY от сервера
            string first = _rd.ReadLine();
            if (first == null || !first.StartsWith("AUTH_READY"))
            {
                Close();
                return "Сервер не ответил на подключение";
            }
            return "";
        }

        // ── Регистрация / вход ──────────────────────────────────────────────────
        // Возвращает пустую строку при успехе, иначе текст ошибки
        public string Register(string nick, string password)
        {
            _wr.WriteLine($"/register {nick} {password}");
            return ReadAuthResult(nick);
        }

        public string Login(string nick, string password)
        {
            _wr.WriteLine($"/login {nick} {password}");
            return ReadAuthResult(nick);
        }

        private string ReadAuthResult(string nick)
        {
            string resp = _rd.ReadLine();
            if (resp == null) return "Соединение прервано";
            if (resp.StartsWith("AUTH_OK:"))
            {
                Nickname   = resp.Substring("AUTH_OK:".Length).Trim();
                _connected = true;
                new Thread(ReadLoop) { IsBackground = true }.Start();
                return "";
            }
            if (resp.StartsWith("AUTH_FAIL:"))
                return resp.Substring("AUTH_FAIL:".Length);
            return "Неожиданный ответ сервера";
        }

        // ── Команды ─────────────────────────────────────────────────────────────
        public void SendPm(string to, string text)        => Send($"/pm {to} {text}");
        public void CreateGroup(string name)              => Send($"/creategroup {name}");
        public void InviteToGroup(string gid, string nick)=> Send($"/invite {gid} {nick}");
        public void AcceptInvite(string gid)              => Send($"/acceptinvite {gid}");
        public void DeclineInvite(string gid)             => Send($"/declineinvite {gid}");
        public void SendGroupMsg(string gid, string text) => Send($"/groupmsg {gid} {text}");
        public void RequestUsers()                        => Send("/users");

        public void Disconnect()
        {
            if (!_connected) return;
            _connected = false;
            try { _wr?.WriteLine("/quit"); } catch { }
            Close();
        }

        private void Close()
        {
            try { _rd?.Close(); }  catch { }
            try { _wr?.Close(); }  catch { }
            try { _tcp?.Close(); } catch { }
        }

        private void Send(string line)
        {
            if (!_connected) return;
            try { _wr.WriteLine(line); }
            catch (Exception ex) { OnSystemMessage?.Invoke($"Ошибка: {ex.Message}"); }
        }

        // ── Чтение ───────────────────────────────────────────────────────────────
        private void ReadLoop()
        {
            try
            {
                string line;
                while (_connected && (line = _rd.ReadLine()) != null)
                    Parse(line);
            }
            catch (IOException) { }
            catch (Exception ex) { OnSystemMessage?.Invoke($"Ошибка: {ex.Message}"); }
            finally { _connected = false; OnDisconnected?.Invoke(); }
        }

        private void Parse(string line)
        {
            if (line.StartsWith("SYSTEM:"))
                OnSystemMessage?.Invoke(line.Substring(7));
            else if (line.StartsWith("USERS:"))
            {
                var p = line.Substring(6);
                OnUsersUpdated?.Invoke(string.IsNullOrEmpty(p) ? new string[0] : p.Split(','));
            }
            else if (line.StartsWith("PM:"))
            {
                var rest = line.Substring(3); int i = rest.IndexOf(':');
                if (i > 0) OnPrivateMessage?.Invoke(rest.Substring(0, i), rest.Substring(i + 1));
            }
            else if (line.StartsWith("PM_SENT:"))
            {
                var rest = line.Substring(8); int i = rest.IndexOf(':');
                if (i > 0) OnPrivateMessageSent?.Invoke(rest.Substring(0, i), rest.Substring(i + 1));
            }
            else if (line.StartsWith("GROUP_CREATED:"))
            {
                var p = line.Substring("GROUP_CREATED:".Length).Split(new[] { ':' }, 2);
                if (p.Length == 2) OnGroupCreated?.Invoke(p[0], p[1]);
            }
            else if (line.StartsWith("INVITE:"))
            {
                var p = line.Substring("INVITE:".Length).Split(new[] { ':' }, 3);
                if (p.Length == 3) OnInviteReceived?.Invoke(p[0], p[1], p[2]);
            }
            else if (line.StartsWith("INVITE_ACCEPTED:"))
            {
                var p = line.Substring("INVITE_ACCEPTED:".Length).Split(new[] { ':' }, 4);
                if (p.Length == 4) OnInviteAccepted?.Invoke(p[0], p[1], p[2], p[3].Split(','));
            }
            else if (line.StartsWith("GROUPMSG:"))
            {
                var p = line.Substring("GROUPMSG:".Length).Split(new[] { ':' }, 3);
                if (p.Length == 3) OnGroupMessage?.Invoke(p[0], p[1], p[2]);
            }
            else if (line.StartsWith("CENSORED:"))
                OnCensored?.Invoke();
            else if (line.StartsWith("HISTORY_PM:"))
            {
                var rest = line.Substring("HISTORY_PM:".Length);
                int i1 = rest.IndexOf(':'); if (i1 < 0) return;
                string peer = rest.Substring(0, i1);
                var tail = rest.Substring(i1 + 1);
                int i2 = tail.IndexOf(':'); if (i2 < 0) return;
                OnHistoryPm?.Invoke(peer, tail.Substring(0, i2), tail.Substring(i2 + 1));
            }
            else if (line.StartsWith("HISTORY_GROUP:"))
            {
                var rest = line.Substring("HISTORY_GROUP:".Length);
                int i1 = rest.IndexOf(':'); if (i1 < 0) return;
                string gid = rest.Substring(0, i1);
                var tail = rest.Substring(i1 + 1);
                int i2 = tail.IndexOf(':'); if (i2 < 0) return;
                OnHistoryGroup?.Invoke(gid, tail.Substring(0, i2), tail.Substring(i2 + 1));
            }
        }
    }
}
