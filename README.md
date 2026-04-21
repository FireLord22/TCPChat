# Лабораторная работа №4 — Многопользовательский TCP-чат

## Структура проекта

```
TCPChat/
├── TCPChat.sln              ← Открыть в Visual Studio
├── ChatServer/
│   ├── ChatServer.csproj
│   ├── ChatServer.cs        ← Серверная логика (TcpListener, потоки, события)
│   ├── MainWindow.xaml      ← WPF-интерфейс сервера
│   ├── MainWindow.xaml.cs
│   ├── App.xaml
│   └── App.xaml.cs
└── ChatClient/
    ├── ChatClient.csproj
    ├── ChatClient.cs        ← Клиентская логика (TcpClient, поток чтения, события)
    ├── MainWindow.xaml      ← WPF-интерфейс клиента
    ├── MainWindow.xaml.cs
    ├── App.xaml
    └── App.xaml.cs
```

## Требования

- Visual Studio 2022
- .NET 6 SDK (или выше)
- Workload: .NET Desktop Development (для WPF)

## Как запустить

1. Откройте `TCPChat.sln` в Visual Studio 2022
2. Правой кнопкой на Solution → **Set Startup Projects** →
   выберите **Multiple startup projects**, оба проекта поставьте **Start**
3. Нажмите **F5**

### Шаг 1 — Сервер
- В окне сервера нажмите **▶ Запустить** (порт по умолчанию 5000)

### Шаг 2 — Клиент(ы)
- Введите адрес (127.0.0.1), порт (5000), никнейм
- Нажмите **Подключиться**
- Запустите второй экземпляр ChatClient для тестирования в паре

## Протокол прикладного уровня

| Команда | Направление | Описание |
|---------|-------------|----------|
| `/join <nickname>` | клиент → сервер | Первое сообщение после подключения |
| `/users` | клиент → сервер | Запрос списка пользователей |
| `/pm <nick> <text>` | клиент → сервер | Личное сообщение |
| `/quit` | клиент → сервер | Корректное отключение |
| `<nick>:<text>` | клиент → сервер | Обычное сообщение |
| `SYSTEM:<text>` | сервер → клиент | Системное уведомление |
| `USERS:<n1,n2,...>` | сервер → клиент | Список онлайн-пользователей |
| `PM:<from>:<text>` | сервер → клиент | Личное сообщение |
| `<nick>:<text>` | сервер → клиент | Широковещательное сообщение |

Каждое сообщение завершается `\n` (StreamWriter/StreamReader с ReadLine).

## Ключевые классы и события

### ChatServer
```csharp
public event Action<string> OnClientConnected;
public event Action<string> OnClientDisconnected;
public event Action<string, string> OnMessageReceived;
public event Action<string> OnLogMessage;
```

### ChatClient
```csharp
public event Action<string, string> OnMessageReceived;
public event Action<string> OnSystemMessage;
public event Action<string[]> OnUsersUpdated;
public event Action OnDisconnected;
public event Action<string, string> OnPrivateMessage;
```
