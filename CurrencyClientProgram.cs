using System.Net.Sockets;
using System.Net;
using System.Text;

class Client
{
    private const int MESSAGE_BUFLEN = 512;
    private const int SERVER_PORT = 27015;

    static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "CURRENCY EXCHANGE CLIENT";
        string? pendingMessage = null;

        var inputCancellation = new CancellationTokenSource();

        while (true)
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, SERVER_PORT);
                Console.WriteLine("Connected to currency exchange server.");
                inputCancellation = new CancellationTokenSource();

                using var stream = client.GetStream();

                var receivingTask = Task.Run(async () =>
                {
                    while (client.Connected)
                    {
                        var buffer = new byte[MESSAGE_BUFLEN];
                        int bytesReceived = await stream.ReadAsync(buffer, 0, buffer.Length);

                        if (bytesReceived > 0)
                        {
                            string response = Encoding.UTF8.GetString(buffer, 0, bytesReceived);
                            Console.WriteLine($"\nServer response:\n{response}");
                        }
                        else
                        {
                            Console.WriteLine("Connection to server lost.");
                            inputCancellation.Cancel();
                            break;
                        }
                    }
                });

                if (pendingMessage != null)
                {
                    byte[] pendingBytes = Encoding.UTF8.GetBytes(pendingMessage);
                    await stream.WriteAsync(pendingBytes, 0, pendingBytes.Length);
                    Console.WriteLine($"Saved message sent: {pendingMessage}");
                    pendingMessage = null;
                }

                while (client.Connected)
                {
                    Console.Write("\nEnter command (USD, EUR, ALL, HELP, EXIT): ");
                    var readTask = Task.Run(() => Console.ReadLine(), inputCancellation.Token);
                    var completedTask = await Task.WhenAny(readTask, receivingTask);

                    if (completedTask == receivingTask)
                    {
                        if (!readTask.IsCompleted)
                        {
                            inputCancellation.Cancel();
                        }
                        pendingMessage = await readTask;
                        break;
                    }

                    var message = readTask.Result;

                    if (string.IsNullOrEmpty(message))
                    {
                        break;
                    }

                    byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                    await stream.WriteAsync(messageBytes, 0, messageBytes.Length);
                    Console.WriteLine($"Command sent: {message}");

                    if (message.ToUpper() == "EXIT")
                    {
                        await Task.Delay(500);
                        break;
                    }
                }

                await receivingTask;
            }
            catch
            {
                Console.WriteLine("Server is unavailable. Trying to reconnect in 3 seconds...");
                await Task.Delay(3000);
            }
        }
    }
}
