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
            
        }

        private void Close()
        {
            
        }

        private void Send(string line)
        {
            
        }

       
        private void ReadLoop()
        {
            
        }

        private void Parse(string line)
        {
            
        }
    }
}
