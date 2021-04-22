using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();
var configBuilder = new ConfigurationBuilder();
configBuilder.AddJsonFile("setting.json", false);
var config = configBuilder.Build();
var ipe = new IPEndPoint(IPAddress.Parse(config["BindAddress"]), int.Parse(config["BindPort"]));
var server = new TcpListener(ipe);
server.Start();
Log.Information($"Start listening at http://{ipe}/.");

void ListenNext(TcpListener listener, string baseAddress)
{
    var client = listener.AcceptTcpClient();
    //Handle next incoming information
    var thread = new Thread(() => ListenNext(listener, baseAddress));
    thread.Start();
    Log.Information($"Incoming connection from {client.Client.RemoteEndPoint}.");
    var stream = client.GetStream();
    try
    {
        Span<byte> buffer = stackalloc byte[512];
        Span<byte> contentBuffer = stackalloc byte[1024];
        stream.Read(buffer);
        var str = Encoding.UTF8.GetString(buffer);
        var firstLine = str.Split("\r\n").First();
        Log.Information($"Receiving {firstLine}");
        var split = firstLine.Split(' ');
        var mime = new FileExtensionContentTypeProvider();
        int headerLen;
        if (split[0] != "GET")
        {
            var bytes = Encoding.UTF8.GetBytes("We don't support method other than GET.", contentBuffer);
            headerLen = Encoding.UTF8.GetBytes(
                $"{split[2]} 400 Bad Request\r\n" +
                $"Content-Length: {bytes}\r\n" +
                "Content-Type: text/html\r\n" +
                "Connection: close\r\n\r\n",
                buffer);
            stream.Write(buffer[..headerLen]);
            stream.Write(contentBuffer[..bytes]);
            Log.Information("Coming request responded with 400.");
            return;
        }

        var path = HttpUtility.UrlDecode(split[1]);
        if (path == "/")
            path = "/index.html";
        var file = new FileInfo(Path.Combine(baseAddress, path[1..]));

        if (!file.Exists)
        {
            headerLen = Encoding.UTF8.GetBytes(
                $"{split[2]} 404 Not Found\r\n" +
                "Content-Length: 0\r\n" +
                "Content-Type: text/html\r\n" +
                "Connection: close\r\n\r\n",
                buffer);
            stream.Write(buffer[..headerLen]);
            Log.Information("Coming request responded with 404.");
            return;
        }

        var len = file.Length;
        var current = 0L;

        headerLen = Encoding.UTF8.GetBytes(
            $"{split[2]} 200 OK\r\n" +
            $"Content-Length: {file.Length}\r\n" +
            $"Content-Type: {mime.Mappings[file.Extension]}\r\n" +
            "Connection: close\r\n\r\n",
            buffer);
        stream.Write(buffer[..headerLen]);
        using var fileStream = file.OpenRead();

        while (current < len)
        {
            var bytes = fileStream.Read(contentBuffer);
            current += bytes;
            stream.Write(bytes == 1024 ? contentBuffer : contentBuffer[..bytes]);
        }

        Log.Information("Coming request responded with 200.");
    }
    catch (Exception e)
    {
        Log.Error(e.ToString());
    }
    finally
    {
        Log.Information("Thread exit.");
        stream.Flush();
    }
}

var serverThread = new Thread(() => ListenNext(server, config["RootFolder"]));
serverThread.Start();