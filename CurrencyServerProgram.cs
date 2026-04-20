using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;
using System.Diagnostics;

class Server
{
    private const int MESSAGE_BUFLEN = 512;
    private const int SERVER_PORT = 27015;
    private static ConcurrentQueue<(TcpClient, byte[])> messageQueue = new ConcurrentQueue<(TcpClient, byte[])>();
    private static TcpListener? listener;
    private static CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
    private static ConcurrentDictionary<int, (TcpClient client, string ip, int port, DateTime connectTime, List<string> requests)> clients = new ConcurrentDictionary<int, (TcpClient, string, int, DateTime, List<string>)>();
    private static int clientCounter = 0;

    private static double usdRate = 41.50;
    private static double eurRate = 44.75;

    static async Task Main()
    {
        string processName = Process.GetCurrentProcess().ProcessName;
        var processes = Process.GetProcessesByName(processName);
        if (processes.Length > 1)
        {
            Console.WriteLine("Server is already running.");
            return;
        }

        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "CURRENCY EXCHANGE SERVER";
        Console.WriteLine("Currency Exchange Server started!");
        Console.WriteLine($"Current rates: USD = {usdRate:F2} UAH, EUR = {eurRate:F2} UAH");
        Console.WriteLine("Admin commands: 'update' - change rates, 'show' - show rates, 'log' - show connection log, 'exit' - stop server");

        _ = Task.Run(async () =>
        {
            while (!cancellationTokenSource.Token.IsCancellationRequested)
            {
                string? input = Console.ReadLine();
                if (input?.ToLower() == "exit")
                {
                    Console.WriteLine("Server process is shutting down...");
                    await StopServerAsync();
                    cancellationTokenSource.Cancel();
                    break;
                }
                else if (input?.ToLower() == "update")
                {
                    await UpdateRatesManually();
                }
                else if (input?.ToLower() == "show")
                {
                    Console.WriteLine($"Current rates - USD: {usdRate:F2} UAH, EUR: {eurRate:F2} UAH");
                }
                else if (input?.ToLower() == "log")
                {
                    ShowConnectionLog();
                }
            }
        }, cancellationTokenSource.Token);

        Console.CancelKeyPress += async (sender, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("Server is shutting down...");
            await StopServerAsync();
            cancellationTokenSource.Cancel();
        };

        try
        {
            listener = new TcpListener(IPAddress.Any, SERVER_PORT);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();
            Console.WriteLine("Please start one or more client programs.");

            _ = ProcessMessages();

            while (!cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    int clientId = Interlocked.Increment(ref clientCounter);
                    var clientEndPoint = (IPEndPoint?)client.Client.RemoteEndPoint;
                    clients.TryAdd(clientId, (client, clientEndPoint!.Address.ToString(), clientEndPoint.Port, DateTime.Now, new List<string>()));
                    Console.WriteLine($"Client #{clientId} connected: IP {clientEndPoint.Address}, Port {clientEndPoint.Port} at {DateTime.Now}");
                    _ = HandleClientAsync(client, clientId);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Accepting new clients stopped.");
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted || ex.SocketErrorCode == SocketError.OperationAborted)
                {
                    Console.WriteLine("Server stopped, accepting new clients stopped.");
                }
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            Console.WriteLine("Port " + SERVER_PORT + " is already in use.");
            Console.ReadKey();
        }
        catch (Exception ex)
        {
            if (!cancellationTokenSource.Token.IsCancellationRequested)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.ReadKey();
            }
        }
        finally
        {
            await StopServerAsync();
            cancellationTokenSource.Dispose();
        }
    }

    private static void ShowConnectionLog()
    {
        Console.WriteLine("\n========== CONNECTION LOG ==========");
        foreach (var client in clients)
        {
            Console.WriteLine($"Client #{client.Key}:");
            Console.WriteLine($"  IP: {client.Value.ip}:{client.Value.port}");
            Console.WriteLine($"  Connected at: {client.Value.connectTime}");
            Console.WriteLine($"  Currencies requested: {(client.Value.requests.Count > 0 ? string.Join(", ", client.Value.requests) : "None")}");
            Console.WriteLine("------------------------------------");
        }
        Console.WriteLine($"Total clients: {clients.Count}");
        Console.WriteLine("====================================\n");
    }

    private static async Task UpdateRatesManually()
    {
        Console.WriteLine("\n=== UPDATE EXCHANGE RATES ===");
        Console.Write($"Current USD rate: {usdRate:F2} UAH. Enter new rate: ");
        string? usdInput = Console.ReadLine();
        if (!string.IsNullOrEmpty(usdInput) && double.TryParse(usdInput, out double newUsdRate))
        {
            usdRate = newUsdRate;
        }

        Console.Write($"Current EUR rate: {eurRate:F2} UAH. Enter new rate: ");
        string? eurInput = Console.ReadLine();
        if (!string.IsNullOrEmpty(eurInput) && double.TryParse(eurInput, out double newEurRate))
        {
            eurRate = newEurRate;
        }
        Console.WriteLine($"Rates updated - USD: {usdRate:F2}, EUR: {eurRate:F2}");
        Console.WriteLine("=============================\n");
    }

    private static async Task HandleClientAsync(TcpClient client, int clientId)
    {
        NetworkStream? stream = null;
        try
        {
            stream = client.GetStream();

            string welcome = "=== CURRENCY EXCHANGE SERVICE ===\nCommands: USD, EUR, ALL, HELP, EXIT\n=================================";
            byte[] welcomeBytes = Encoding.UTF8.GetBytes(welcome);
            await stream.WriteAsync(welcomeBytes, 0, welcomeBytes.Length, cancellationTokenSource.Token);

            while (!cancellationTokenSource.Token.IsCancellationRequested && client.Connected)
            {
                var buffer = new byte[MESSAGE_BUFLEN];
                int bytesReceived = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationTokenSource.Token).ConfigureAwait(false);

                if (bytesReceived > 0)
                {
                    messageQueue.Enqueue((client, buffer[..bytesReceived]));
                    Console.WriteLine($"Client #{clientId}: Added message to queue.");
                }
                else
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"Client #{clientId}: Operation cancelled.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error with client #{clientId}: {ex.Message}");
        }
        finally
        {
            stream?.Dispose();
            client.Close();

            if (clients.TryGetValue(clientId, out var clientInfo))
            {
                Console.WriteLine($"Client #{clientId} disconnected at {DateTime.Now}");
                clients.TryRemove(clientId, out _);
            }
            Console.WriteLine($"Client #{clientId} disconnected.");
        }
    }

    private static async Task StopServerAsync()
    {
        try
        {
            cancellationTokenSource.Cancel();

            foreach (var clientInfo in clients.Values)
            {
                try
                {
                    clientInfo.client.Close();
                    clientInfo.client.Dispose();
                    Console.WriteLine($"Client with IP {clientInfo.ip}:{clientInfo.port} closed.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error closing client {clientInfo.ip}:{clientInfo.port}: {ex.Message}");
                }
            }
            clients.Clear();

            listener?.Stop();
            listener = null;

            Console.WriteLine("Server completely stopped.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error stopping server: {ex.Message}");
        }
        finally
        {
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    private static async Task ProcessMessages()
    {
        while (!cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                if (messageQueue.TryDequeue(out var item))
                {
                    var (client, buffer) = item;
                    if (!client.Connected) continue;

                    string command = Encoding.UTF8.GetString(buffer).Trim().ToUpper();
                    var clientEndPoint = (IPEndPoint?)client.Client.RemoteEndPoint;
                    int clientId = clients.FirstOrDefault(x => x.Value.ip == clientEndPoint?.Address.ToString() && x.Value.port == clientEndPoint.Port).Key;

                    if (command == "USD" || command == "EUR" || command == "ALL")
                    {
                        if (clients.TryGetValue(clientId, out var clientData))
                        {
                            clientData.requests.Add(command);
                            clients[clientId] = clientData;
                            Console.WriteLine($"Client #{clientId} requested: {command} at {DateTime.Now}");
                        }
                    }

                    Console.WriteLine($"Client #{clientId} sent command: {command}");

                    string response = ProcessCommand(command);

                    await Task.Delay(100, cancellationTokenSource.Token).ConfigureAwait(false);

                    byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                    try
                    {
                        var stream = client.GetStream();
                        await stream.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationTokenSource.Token).ConfigureAwait(false);
                        Console.WriteLine($"Response sent to client #{clientId}");
                    }
                    catch
                    {
                        Console.WriteLine($"Failed to send message to client #{clientId}.");
                    }
                }

                await Task.Delay(15, cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in ProcessMessages: {ex.Message}");
            }
        }
    }

    private static string ProcessCommand(string command)
    {
        switch (command)
        {
            case "USD":
                return $"USD to UAH: {usdRate:F2} UAH";
            case "EUR":
                return $"EUR to UAH: {eurRate:F2} UAH";
            case "ALL":
                return $"Current exchange rates:\nUSD: {usdRate:F2} UAH\nEUR: {eurRate:F2} UAH";
            case "HELP":
                return "Available commands:\nUSD - get USD rate\nEUR - get EUR rate\nALL - get all rates\nEXIT - disconnect";
            case "EXIT":
                return "Goodbye! Disconnecting...";
            default:
                return $"Unknown command: {command}. Type HELP for available commands.";
        }
    }
}
