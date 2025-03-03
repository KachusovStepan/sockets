﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Web;

namespace Sockets
{
    class Program
    {
        static void Main(string[] args)
        {
            AsynchronousSocketListener.StartListening();
        }
    }

    public class AsynchronousSocketListener
    {
        private const int listeningPort = 11000;
        private static ManualResetEvent connectionEstablished = new ManualResetEvent(false);

        private class ReceivingState
        {
            public Socket ClientSocket;
            public const int BufferSize = 1024;
            public readonly byte[] Buffer = new byte[BufferSize];
            public readonly List<byte> ReceivedData = new List<byte>();
        }

        public static void StartListening()
        {
            // Определяем IP-адрес, по которому будем принимать сообщения.
            // Для этого сначала получаем DNS-имя компьютера,
            // а из всех адресов выбираем первый попавшийся IPv4 адрес.
            string hostName = Dns.GetHostName();
            IPHostEntry ipHostEntry = Dns.GetHostEntry(hostName);
            IPAddress ipV4Address = ipHostEntry.AddressList
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .OrderBy(address => address.ToString())
                .FirstOrDefault();
            if (ipV4Address == null)
            {
                Console.WriteLine(">>> Can't find IPv4 address for host");
                return;
            }
            // По выбранному IP-адресу будем слушать listeningPort.
            IPEndPoint ipEndPoint = new IPEndPoint(ipV4Address, listeningPort);

            // Создаем TCP/IP сокет для приема соединений.
            Socket connectionSocket = new Socket(ipV4Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                // Присоединяем сокет к выбранной конечной точке (IP-адресу и порту).
                connectionSocket.Bind(ipEndPoint);
                // Начинаем слушать, в очереди на установку соединений не более 100 клиентов.
                connectionSocket.Listen(100);

                // Принимаем входящие соединения.
                while (true)
                    Accept(connectionSocket);
            }
            catch (Exception e)
            {
                Console.WriteLine(">>> Got exception:");
                Console.WriteLine(e.ToString());
                Console.WriteLine(">>> ");
            }
        }

        private static void Accept(Socket connectionSocket)
        {
            // Сбрасываем состояние события установки соединения: теперь оно "не произошло".
            // Это событие используется для синхронизации потоков.
            connectionEstablished.Reset();

            // Начинаем слушать асинхронно, ожидая входящих соединений.
            // Вторым параметром передаем объект, который будет передан в callback.
            connectionSocket.BeginAccept(AcceptCallback, connectionSocket);
            Console.WriteLine($">>> Waiting for a connection to http://{connectionSocket.LocalEndPoint}");

            // Поток, в котором начали слушать connectionSocket будет ждать,
            // пока кто-нибудь не установит событие connectionEstablished.
            // Это произойдет в AcceptCallback, когда соединение будет установлено.
            connectionEstablished.WaitOne();
        }

        private static void AcceptCallback(IAsyncResult asyncResult)
        {
            // Соединение установлено, сигнализируем основному потоку,
            // чтобы он продолжил принимать соединения.
            connectionEstablished.Set();

            // Получаем сокет к клиенту, с которым установлено соединение.
            Socket connectionSocket = (Socket)asyncResult.AsyncState;
            Socket clientSocket = connectionSocket.EndAccept(asyncResult);

            // Принимаем данные от клиента.
            Receive(clientSocket);
        }

        private static void Receive(Socket clientSocket)
        {
            // Создаем объект для callback.
            ReceivingState receivingState = new ReceivingState();
            receivingState.ClientSocket = clientSocket;
            // Начинаем асинхронно получать данные от клиента.
            // Передаем буфер, куда будут складываться полученные байты.
            clientSocket.BeginReceive(receivingState.Buffer, 0, ReceivingState.BufferSize, SocketFlags.None,
                ReceiveCallback, receivingState);
        }

        private static void ReceiveCallback(IAsyncResult asyncResult)
        {
            // Достаем клиентский сокет из параметра callback.
            ReceivingState receivingState = (ReceivingState)asyncResult.AsyncState;
            Socket clientSocket = receivingState.ClientSocket;

            // Читаем данные из клиентского сокета.
            int bytesReceived = clientSocket.EndReceive(asyncResult);

            if (bytesReceived > 0)
            {
                // В буфер могли поместиться не все данные.
                // Все данные от клиента складываем в другой буфер - ReceivedData.
                receivingState.ReceivedData.AddRange(receivingState.Buffer.Take(bytesReceived));

                // Пытаемся распарсить Request из полученных данных.
                byte[] receivedBytes = receivingState.ReceivedData.ToArray();
                Request request = Request.StupidParse(receivedBytes);
                if (request == null)
                {
                    // request не распарсился, значит получили не все данные.
                    // Запрашиваем еще.
                    clientSocket.BeginReceive(receivingState.Buffer, 0, ReceivingState.BufferSize, SocketFlags.None,
                        ReceiveCallback, receivingState);
                }
                else
                {
                    // Все данные были получены от клиента.
                    // Для удобства выведем их на консоль.
                    Console.WriteLine($">>> Received {receivedBytes.Length} bytes from {clientSocket.RemoteEndPoint}. Data:\n" +
                        Encoding.ASCII.GetString(receivedBytes));

                    // Сформируем ответ.
                    byte[] responseBytes = ProcessRequest(request);

                    // Отправим ответ клиенту.
                    Send(clientSocket, responseBytes);
                }
            }
        }

        private static byte[] ProcessRequest(Request request)
        {
            // TODO
            var head = new StringBuilder();
            var body = new byte[0];
            var requestUri = request.RequestUri;
            // Console.WriteLine(requestUri);
            NameValueCollection parameters = null;
            var cookies = new Dictionary<string, string>();
            
            // Get Cookie
            foreach (var header in request.Headers)
            {
                Console.WriteLine($"{header.Name}: {header.Value}");
                if (header.Name.ToLower() == "cookie")
                {
                    var value = HttpUtility.UrlDecode(header.Value);
                    var cookiesNameValuePairs = value.Split(";")
                        .Select(s => s.Trim())
                        .Select(cookiePair => cookiePair.Split("="));
                    
                    foreach (var cookieNameValue in cookiesNameValuePairs)
                    {
                        if (cookieNameValue.Length == 2)
                        {
                            cookies[cookieNameValue[0]] = cookieNameValue[1];
                        }
                    }

                }
            }
            
            // Get Query Parameters
            if (requestUri.Contains("?"))
            {
                var parametersStart = requestUri.IndexOf("?") + 1;
                var parameterSubstring = requestUri.Substring(parametersStart);
                parameters = HttpUtility.ParseQueryString(parameterSubstring);
            }
            if (requestUri == "/" || requestUri.StartsWith("/?") || requestUri.StartsWith("/hello.html"))
            {
                body = File.ReadAllBytes("hello.html");
                head.Append("HTTP/1.1 200 OK\r\n");
                head.Append("Content-Type: text/html; charset=utf-8\r\n");
                String? name = null;
                String? greeting = null;
                
                if (cookies.ContainsKey("name"))
                {
                    name = cookies["name"];
                }

                if (cookies.ContainsKey("greeting"))
                {
                    greeting = cookies["greeting"];
                }

                if (parameters != null)
                {
                    if (parameters["greeting"] != null)
                    {
                        greeting = parameters["greeting"];
                        head.Append($"Set-Cookie: greeting={HttpUtility.UrlEncode(HttpUtility.HtmlEncode(greeting))}\r\n");
                    }
                
                    if (parameters["name"] != null)
                    {
                        name = parameters["name"];
                        head.Append($"Set-Cookie: name={HttpUtility.UrlEncode(HttpUtility.HtmlEncode(name))}\r\n");
                    }
                }

                var stringContent = Encoding.UTF8.GetString(body);

                var replaced = stringContent;
                if (greeting != null)
                {
                    var safeReplacement = HttpUtility.HtmlEncode(greeting);
                    replaced = replaced.Replace("{{Hello}}", safeReplacement);
                }

                if (name != null)
                {
                    var safeReplacement = HttpUtility.HtmlEncode(name);
                    replaced = replaced.Replace("{{World}}", safeReplacement);
                    
                }

                body = Encoding.UTF8.GetBytes(replaced);
                
            } else if (requestUri == "/groot.gif")
            {
                head.Append("HTTP/1.1 200 OK\r\n");
                head.Append("Content-Type: image/gif\r\n");
                
                body = File.ReadAllBytes("groot.gif");
            } else if (requestUri == "/time.html")
            {
                var rawContent = File.ReadAllBytes("time.template.html");
                var stringContent = Encoding.UTF8.GetString(rawContent);
                var replaced = stringContent.Replace("{{ServerTime}}", DateTime.Now.ToString());
                body = Encoding.UTF8.GetBytes(replaced);
                
                head.Append("HTTP/1.1 200 OK\r\n");
                head.Append("Content-Type: text/html; charset=utf-8\r\n");
            }
            else
            {
                head.Append("HTTP/1.1 404 Not Found\r\n");
                
                body = new byte[0];
            }
            head.Append($"Content-Length: {body.Length}\r\n");
            head.Append("\r\n");

            Console.WriteLine(head);
            return CreateResponseBytes(head, body);
        }

        // Собирает ответ в виде массива байт из байтов строки head и байтов body.
        private static byte[] CreateResponseBytes(StringBuilder head, byte[] body)
        {
            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            byte[] responseBytes = new byte[headBytes.Length + body.Length];
            Array.Copy(headBytes, responseBytes, headBytes.Length);
            Array.Copy(body, 0,
                responseBytes, headBytes.Length,
                body.Length);
            return responseBytes;
        }

        private static void Send(Socket clientSocket, byte[] responseBytes)
        {
            Console.WriteLine(">>> Sending {0} bytes to client socket.", responseBytes.Length);
            // Начинаем асинхронно отправлять данные клиенту.
            clientSocket.BeginSend(responseBytes, 0, responseBytes.Length, SocketFlags.None,
                SendCallback, clientSocket);
        }

        private static void SendCallback(IAsyncResult asyncResult)
        {
            // Достаем клиентский сокет из параметра callback.
            Socket clientSocket = (Socket)asyncResult.AsyncState;
            try
            {
                // Завершаем отправку данных клиенту.
                int bytesSent = clientSocket.EndSend(asyncResult);
                Console.WriteLine(">>> Sent {0} bytes to client.", bytesSent);

                // Закрываем соединение.
                clientSocket.Shutdown(SocketShutdown.Both);
                clientSocket.Close();
                Console.WriteLine(">>> ");
            }
            catch (Exception e)
            {
                Console.WriteLine(">>> Got exception:");
                Console.WriteLine(e.ToString());
                Console.WriteLine(">>> ");
            }
        }
    }
}
