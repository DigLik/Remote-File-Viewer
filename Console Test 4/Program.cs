using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;
using System.Collections.Generic;
using System.IO.Compression;
using System.Threading.Tasks;

class SimpleFileServer
{
    private static readonly string ipAddress = "192.168.100.3";
    private static readonly int port = 8080;

    static void Main(string[] args)
    {
        IPAddress localAddr = IPAddress.Parse(ipAddress);
        TcpListener listener = new TcpListener(localAddr, port);

        try
        {
            listener.Start();
            Console.WriteLine($"Сервер запущен на http://{ipAddress}:{port}/");
            Console.WriteLine("Ожидание подключений...");

            while (true)
            {
                TcpClient client = listener.AcceptTcpClient();
                _ = Task.Run(() => HandleClient(client));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка: {ex.Message}");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void HandleClient(TcpClient client)
    {
        using (client)
        using (NetworkStream stream = client.GetStream())
        {
            try
            {
                byte[] buffer = new byte[8192];
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) return;
                string request = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                string[] lines = request.Split('\n');
                if (lines.Length == 0) { SendResponse(stream, "400 Bad Request", "Bad Request"); return; }
                string[] requestLine = lines[0].Split(' ');
                if (requestLine.Length < 3 || (requestLine[0] != "GET" && requestLine[0] != "POST"))
                {
                    SendResponse(stream, "400 Bad Request", "Bad Request");
                    return;
                }
                string method = requestLine[0];
                string url = requestLine[1];
                string path = "";
                string queryString = "";
                int queryIndex = url.IndexOf('?');
                if (queryIndex >= 0)
                {
                    path = url.Substring(0, queryIndex);
                    queryString = url.Substring(queryIndex + 1);
                }
                else
                {
                    path = url;
                }
                var queryParams = ParseQueryString(queryString);
                string requestedPath = "";
                if (queryParams.ContainsKey("path"))
                {
                    requestedPath = queryParams["path"];
                }
                if (string.IsNullOrEmpty(requestedPath))
                {
                    ShowDrives(stream);
                    return;
                }
                if (requestedPath.Contains(".."))
                {
                    SendResponse(stream, "403 Forbidden", "Доступ запрещён");
                    return;
                }
                string normalizedPath = requestedPath.Replace('/', Path.DirectorySeparatorChar);
                if (normalizedPath.Length == 2 && normalizedPath[1] == ':')
                {
                    normalizedPath += Path.DirectorySeparatorChar;
                }
                string fullPath = Path.GetFullPath(normalizedPath);
                if (!IsPathAllowed(fullPath))
                {
                    SendResponse(stream, "403 Forbidden", "Доступ запрещён");
                    return;
                }
                bool isDownload = queryParams.ContainsKey("download") && queryParams["download"] == "1";
                bool isPreview = queryParams.ContainsKey("preview") && queryParams["preview"] == "1";
                bool isRaw = queryParams.ContainsKey("raw") && queryParams["raw"] == "1";
                if (method == "POST" && url.StartsWith("/download"))
                {
                    HandleMultiFileDownload(stream, request);
                    return;
                }
                if (Directory.Exists(fullPath))
                {
                    ShowDirectoryListing(stream, fullPath);
                }
                else if (File.Exists(fullPath))
                {
                    if (isRaw)
                    {
                        SendRawFileWithRange(stream, request, fullPath);
                    }
                    else if (isDownload)
                    {
                        SendFile(stream, fullPath);
                    }
                    else if (isPreview)
                    {
                        ShowFilePreview(stream, fullPath);
                    }
                    else
                    {
                        ShowFilePreview(stream, fullPath);
                    }
                }
                else
                {
                    SendResponse(stream, "404 Not Found", "Файл или директория не найдены");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при обработке клиента: {ex.Message}");
                try
                {
                    SendResponse(stream, "500 Internal Server Error", "Внутренняя ошибка сервера");
                }
                catch { }
            }
        }
    }

    private static Dictionary<string, string> ParseQueryString(string queryString)
    {
        var queryParams = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(queryString))
        {
            var pairs = queryString.Split('&');
            foreach (var pair in pairs)
            {
                var keyValue = pair.Split(new[] { '=' }, 2);
                if (keyValue.Length == 2)
                {
                    string key = Uri.UnescapeDataString(keyValue[0]);
                    string value = Uri.UnescapeDataString(keyValue[1]);
                    queryParams[key] = value;
                }
                else if (keyValue.Length == 1)
                {
                    string key = Uri.UnescapeDataString(keyValue[0]);
                    queryParams[key] = "";
                }
            }
        }
        return queryParams;
    }

    private static bool IsPathAllowed(string path)
    {
        return true;
    }

    private static void ShowDrives(NetworkStream stream)
    {
        var drives = DriveInfo.GetDrives();
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"UTF-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"><title>Диски</title><style>");
        sb.Append(GetCssStyles());
        sb.AppendLine("</style><script>");
        sb.Append(GetJavaScript());
        sb.AppendLine("</script></head><body>");
        sb.AppendLine(GetHeader());
        sb.AppendLine("<div class=\"main-content\">");
        sb.AppendLine("<div class=\"actions\"><button onclick=\"downloadSelectedFiles()\">⬇️ Скачать выбранные файлы</button></div>");
        sb.AppendLine("<div class=\"file-list\">");
        foreach (var drive in drives)
        {
            if (drive.IsReady)
            {
                string driveName = drive.Name;
                string driveLabel = string.IsNullOrEmpty(drive.VolumeLabel) ? "Локальный диск" : drive.VolumeLabel;
                sb.AppendLine($@"
                <div class=""file-item"" onclick=""navigateTo('{Uri.EscapeDataString(driveName)}')"">
                    <input type=""checkbox"" class=""file-checkbox"" data-path=""{Uri.EscapeDataString(driveName)}"" disabled>
                    <div class=""item-icon"">💽</div>
                    <div class=""item-text"" title=""{HttpUtility.HtmlEncode(driveLabel)} ({driveName.TrimEnd(Path.DirectorySeparatorChar)})"">{TruncateText(HttpUtility.HtmlEncode(driveLabel) + $" ({driveName.TrimEnd(Path.DirectorySeparatorChar)})", 30)}</div>
                </div>");
            }
        }
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");
        sb.AppendLine("<div class=\"file-preview\" id=\"filePreview\"><div id=\"previewContent\"></div></div>");
        sb.AppendLine("</body></html>");
        SendResponse(stream, "200 OK", sb.ToString(), "text/html; charset=utf-8");
    }

    private static void ShowDirectoryListing(NetworkStream stream, string directoryPath)
    {
        try
        {
            DirectoryInfo dirInfo = new DirectoryInfo(directoryPath);
            var directories = dirInfo.GetDirectories();
            var files = dirInfo.GetFiles();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"UTF-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"><title>Директория</title><style>");
            sb.Append(GetCssStyles());
            sb.AppendLine("</style><script>");
            sb.Append(GetJavaScript());
            sb.AppendLine("</script></head><body>");
            sb.AppendLine(GetHeader());
            sb.AppendLine("<div class=\"main-content\">");
            sb.AppendLine("<div class=\"breadcrumbs\">");
            sb.Append(GetBreadcrumbs(directoryPath));
            sb.AppendLine("</div>");
            sb.AppendLine("<div class=\"actions\"><button onclick=\"downloadSelectedFiles()\">⬇️ Скачать выбранные файлы</button></div>");
            sb.AppendLine("<div class=\"file-list\">");
            string parentPath = GetParentPath(directoryPath);
            if (parentPath != null)
            {
                sb.AppendLine($@"
                <div class=""file-item"" onclick=""navigateTo('{Uri.EscapeDataString(parentPath)}')"">
                    <input type=""checkbox"" class=""file-checkbox"" data-path=""{Uri.EscapeDataString(parentPath)}"" disabled>
                    <div class=""item-icon"">⬅️</div>
                    <div class=""item-text"">Назад</div>
                </div>");
            }
            foreach (var dir in directories)
            {
                string dirPath = dir.FullName + Path.DirectorySeparatorChar;
                sb.AppendLine($@"
                <div class=""file-item"" onclick=""navigateTo('{Uri.EscapeDataString(dirPath)}')"">
                    <input type=""checkbox"" class=""file-checkbox"" data-path=""{Uri.EscapeDataString(dirPath)}"" disabled>
                    <div class=""item-icon"">📁</div>
                    <div class=""item-text"" title=""{HttpUtility.HtmlEncode(dir.Name)}"">{TruncateText(HttpUtility.HtmlEncode(dir.Name), 30)}</div>
                    <div class=""item-size"">{dir.LastWriteTime.ToString("dd.MM.yyyy HH:mm")}</div>
                </div>");
            }
            foreach (var file in files)
            {
                string mimeType = GetMimeType(file.FullName);
                string icon = GetIconForFile(mimeType);
                string filePathEncoded = Uri.EscapeDataString(file.FullName);
                sb.AppendLine($@"
                <div class=""file-item"" onclick=""showPreview(event, '{filePathEncoded}')"">
                    <input type=""checkbox"" class=""file-checkbox"" data-path=""{filePathEncoded}"">
                    <div class=""item-icon"">{icon}</div>
                    <div class=""item-text"" title=""{HttpUtility.HtmlEncode(file.Name)}"">{TruncateText(HttpUtility.HtmlEncode(file.Name), 30)}</div>
                    <div class=""item-size"">{FormatFileSize(file.Length)} • {file.LastWriteTime.ToString("dd.MM.yyyy HH:mm")}</div>
                </div>");
            }
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("<div class=\"file-preview\" id=\"filePreview\"><div id=\"previewContent\"></div></div>");
            sb.AppendLine("</body></html>");
            SendResponse(stream, "200 OK", sb.ToString(), "text/html; charset=utf-8");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при доступе к директории: {ex.Message}");
            SendResponse(stream, "500 Internal Server Error", $"Ошибка при доступе к директории: {ex.Message}");
        }
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;
        else
            return text.Substring(0, maxLength - 3) + "...";
    }

    private static string GetIconForFile(string mimeType)
    {
        if (mimeType.StartsWith("image/"))
            return "🖼️";
        else if (mimeType.StartsWith("video/"))
            return "🎞️";
        else if (mimeType.StartsWith("audio/"))
            return "🎵";
        else if (mimeType == "application/pdf")
            return "📄";
        else if (mimeType == "application/msword" || mimeType == "application/vnd.openxmlformats-officedocument.wordprocessingml.document")
            return "📄";
        else if (mimeType == "application/vnd.ms-excel" || mimeType == "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
            return "📊";
        else if (mimeType == "application/vnd.ms-powerpoint" || mimeType == "application/vnd.openxmlformats-officedocument.presentationml.presentation")
            return "📽️";
        else if (mimeType.StartsWith("text/") || mimeType == "application/json" || mimeType == "application/xml")
            return "📄";
        else
            return "📄";
    }

    private static void ShowFilePreview(NetworkStream stream, string filePath)
    {
        try
        {
            string mimeType = GetMimeType(filePath);
            FileInfo fileInfo = new FileInfo(filePath);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"UTF-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"><title>Предпросмотр</title><style>");
            sb.Append(GetCssStyles());
            sb.AppendLine("</style></head><body>");
            sb.AppendLine("<div class=\"file-preview-content\">");
            sb.AppendLine("<button class=\"close-btn\" onclick=\"window.parent.closePreview()\">Закрыть</button>");
            if (mimeType.StartsWith("image/"))
            {
                string imageUrl = $"/?path={Uri.EscapeDataString(filePath)}&raw=1";
                sb.AppendLine($"<img src=\"{imageUrl}\" class=\"preview-image\" />");
            }
            else if (mimeType.StartsWith("video/"))
            {
                string videoUrl = $"/?path={Uri.EscapeDataString(filePath)}&raw=1";
                sb.AppendLine($"<video controls class=\"preview-video\"><source src=\"{videoUrl}\" type=\"{mimeType}\">Ваш браузер не поддерживает воспроизведение видео.</video>");
            }
            else if (mimeType.StartsWith("audio/"))
            {
                string audioUrl = $"/?path={Uri.EscapeDataString(filePath)}&raw=1";
                sb.AppendLine($"<audio controls class=\"preview-audio\"><source src=\"{audioUrl}\" type=\"{mimeType}\">Ваш браузер не поддерживает воспроизведение аудио.</audio>");
            }
            else if (mimeType == "application/pdf")
            {
                string pdfUrl = $"/?path={Uri.EscapeDataString(filePath)}&raw=1";
                sb.AppendLine($"<embed src=\"{pdfUrl}\" type=\"application/pdf\" width=\"100%\" height=\"600px\" />");
            }
            else if (mimeType.StartsWith("text/") || mimeType == "application/json" || mimeType == "application/xml")
            {
                string fileContent = File.ReadAllText(filePath, Encoding.UTF8);
                fileContent = HttpUtility.HtmlEncode(fileContent);
                sb.AppendLine("<pre>" + fileContent + "</pre>");
            }
            else
            {
                sb.AppendLine("<p>Предпросмотр этого файла недоступен.</p>");
            }
            sb.AppendLine($"<div class=\"file-metadata\"><p>Имя файла: {HttpUtility.HtmlEncode(fileInfo.Name)}</p><p>Размер файла: {FormatFileSize(fileInfo.Length)}</p><p>Последнее изменение: {fileInfo.LastWriteTime.ToString("dd.MM.yyyy HH:mm")}</p></div>");
            sb.AppendLine($"<a href=\"/?path={Uri.EscapeDataString(filePath)}&download=1\" class=\"download-btn\">⬇️ Скачать файл</a>");
            sb.AppendLine("</div></body></html>");
            SendResponse(stream, "200 OK", sb.ToString(), "text/html; charset=utf-8");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при отображении файла: {ex.Message}");
            SendResponse(stream, "500 Internal Server Error", $"Ошибка при отображении файла: {ex.Message}");
        }
    }

    private static string GetBreadcrumbs(string path)
    {
        StringBuilder sb = new StringBuilder();
        string[] parts = path.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        string cumulativePath = "";
        sb.Append("<a href=\"/\">Главная</a>");
        string drive = Path.GetPathRoot(path);
        cumulativePath = drive;
        sb.Append($" / <a href=\"/?path={Uri.EscapeDataString(cumulativePath)}\">{HttpUtility.HtmlEncode(drive.TrimEnd(Path.DirectorySeparatorChar))}</a>");
        string[] subPaths = path.Substring(drive.Length).Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in subPaths)
        {
            cumulativePath = Path.Combine(cumulativePath, part);
            sb.Append($" / <a href=\"/?path={Uri.EscapeDataString(cumulativePath + Path.DirectorySeparatorChar)}\">{HttpUtility.HtmlEncode(part)}</a>");
        }
        return sb.ToString();
    }

    private static string GetParentPath(string path)
    {
        try
        {
            DirectoryInfo dir = new DirectoryInfo(path);
            if (dir.Parent != null)
            {
                return dir.Parent.FullName + Path.DirectorySeparatorChar;
            }
            else
            {
                return Path.GetPathRoot(path);
            }
        }
        catch
        {
            return null;
        }
    }

    private static void SendRawFileWithRange(NetworkStream stream, string request, string filePath)
    {
        try
        {
            FileInfo fileInfo = new FileInfo(filePath);
            long totalLength = fileInfo.Length;
            string mimeType = GetMimeType(filePath);
            string rangeHeader = GetHeaderValue(request, "Range");
            if (string.IsNullOrEmpty(rangeHeader))
            {
                SendRawFile(stream, filePath);
                return;
            }
            string[] range = rangeHeader.Replace("bytes=", "").Split('-');
            long start = long.Parse(range[0]);
            long end = range.Length > 1 && !string.IsNullOrEmpty(range[1]) ? long.Parse(range[1]) : totalLength - 1;
            if (end >= totalLength) end = totalLength - 1;
            long contentLength = end - start + 1;
            byte[] buffer = new byte[8192];
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                fs.Seek(start, SeekOrigin.Begin);
                string header = "HTTP/1.1 206 Partial Content\r\n" +
                                $"Content-Range: bytes {start}-{end}/{totalLength}\r\n" +
                                "Accept-Ranges: bytes\r\n" +
                                "Connection: keep-alive\r\n" +
                                "Content-Length: " + contentLength + "\r\n" +
                                $"Content-Type: {mimeType}\r\n" +
                                "\r\n";
                byte[] headerBytes = Encoding.UTF8.GetBytes(header);
                stream.Write(headerBytes, 0, headerBytes.Length);
                long remaining = contentLength;
                int read;
                while (remaining > 0 && (read = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
                {
                    stream.Write(buffer, 0, read);
                    remaining -= read;
                }
            }
            Console.WriteLine($"Отправлен файл с Range: {filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при отправке файла с Range: {ex.Message}");
            SendResponse(stream, "500 Internal Server Error", "Ошибка при отправке файла");
        }
    }

    private static string GetHeaderValue(string request, string headerName)
    {
        string[] lines = request.Split('\n');
        foreach (var line in lines)
        {
            if (line.StartsWith(headerName, StringComparison.OrdinalIgnoreCase))
            {
                int index = line.IndexOf(':');
                if (index >= 0)
                {
                    return line.Substring(index + 1).Trim();
                }
            }
        }
        return null;
    }

    private static void SendRawFile(NetworkStream stream, string filePath)
    {
        try
        {
            byte[] buffer = new byte[8192];
            string mimeType = GetMimeType(filePath);
            FileInfo fileInfo = new FileInfo(filePath);
            long totalLength = fileInfo.Length;
            string header = "HTTP/1.1 200 OK\r\n" +
                            "Accept-Ranges: bytes\r\n" +
                            "Connection: keep-alive\r\n" +
                            "Content-Length: " + totalLength + "\r\n" +
                            $"Content-Type: {mimeType}\r\n" +
                            "\r\n";
            byte[] headerBytes = Encoding.UTF8.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                int read;
                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    stream.Write(buffer, 0, read);
                }
            }
            Console.WriteLine($"Отправлен файл (raw): {filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при отправке файла: {ex.Message}");
            SendResponse(stream, "500 Internal Server Error", "Ошибка при отправке файла");
        }
    }

    private static void SendFile(NetworkStream stream, string filePath)
    {
        try
        {
            byte[] fileBytes = File.ReadAllBytes(filePath);
            string fileName = Path.GetFileName(filePath);
            string fileNameEncoded = Uri.EscapeDataString(fileName);
            string mimeType = GetMimeType(filePath);
            string header = "HTTP/1.1 200 OK\r\n" +
                            "Content-Length: " + fileBytes.Length + "\r\n" +
                            $"Content-Type: {mimeType}\r\n" +
                            $"Content-Disposition: attachment; filename=\"{fileName}\"; filename*=UTF-8''{fileNameEncoded}\r\n" +
                            "\r\n";
            byte[] headerBytes = Encoding.UTF8.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(fileBytes, 0, fileBytes.Length);
            Console.WriteLine($"Отправлен файл: {filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при отправке файла: {ex.Message}");
            SendResponse(stream, "500 Internal Server Error", "Ошибка при отправке файла");
        }
    }

    private static void HandleMultiFileDownload(NetworkStream stream, string request)
    {
        try
        {
            string body = request.Substring(request.IndexOf("\r\n\r\n") + 4);
            var queryParams = ParseQueryString(body);
            if (queryParams.ContainsKey("files"))
            {
                string[] files = queryParams["files"].Split(',');
                using (MemoryStream ms = new MemoryStream())
                {
                    using (ZipArchive zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    {
                        foreach (var file in files)
                        {
                            string filePath = Uri.UnescapeDataString(file);
                            if (File.Exists(filePath))
                            {
                                zip.CreateEntryFromFile(filePath, Path.GetFileName(filePath));
                            }
                        }
                    }
                    byte[] zipBytes = ms.ToArray();
                    string header = "HTTP/1.1 200 OK\r\n" +
                                    "Content-Length: " + zipBytes.Length + "\r\n" +
                                    "Content-Type: application/zip\r\n" +
                                    "Content-Disposition: attachment; filename=\"files.zip\"\r\n" +
                                    "\r\n";
                    byte[] headerBytes = Encoding.UTF8.GetBytes(header);
                    stream.Write(headerBytes, 0, headerBytes.Length);
                    stream.Write(zipBytes, 0, zipBytes.Length);
                }
            }
            else
            {
                SendResponse(stream, "400 Bad Request", "Нет файлов для загрузки");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при загрузке нескольких файлов: {ex.Message}");
            SendResponse(stream, "500 Internal Server Error", "Ошибка при загрузке файлов");
        }
    }

    private static void SendResponse(NetworkStream stream, string status, string message, string contentType = "text/plain; charset=utf-8")
    {
        string response = $"HTTP/1.1 {status}\r\n" +
                          $"Content-Type: {contentType}\r\n" +
                          $"Content-Length: {Encoding.UTF8.GetBytes(message).Length}\r\n" +
                          "\r\n" +
                          message;
        byte[] responseBytes = Encoding.UTF8.GetBytes(response);
        stream.Write(responseBytes, 0, responseBytes.Length);
    }

    private static string GetMimeType(string filePath)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        switch (extension)
        {
            case ".html":
            case ".htm":
                return "text/html";
            case ".css":
                return "text/css";
            case ".js":
                return "application/javascript";
            case ".json":
                return "application/json";
            case ".xml":
                return "application/xml";
            case ".txt":
                return "text/plain";
            case ".png":
                return "image/png";
            case ".jpg":
            case ".jpeg":
                return "image/jpeg";
            case ".gif":
                return "image/gif";
            case ".bmp":
                return "image/bmp";
            case ".svg":
                return "image/svg+xml";
            case ".webp":
                return "image/webp";
            case ".mp4":
                return "video/mp4";
            case ".webm":
                return "video/webm";
            case ".ogg":
                return "video/ogg";
            case ".mov":
                return "video/quicktime";
            case ".avi":
                return "video/x-msvideo";
            case ".wmv":
                return "video/x-ms-wmv";
            case ".mkv":
                return "video/x-matroska";
            case ".flv":
                return "video/x-flv";
            case ".mp3":
                return "audio/mpeg";
            case ".wav":
                return "audio/wav";
            case ".flac":
                return "audio/flac";
            case ".aac":
                return "audio/aac";
            case ".m4a":
                return "audio/mp4";
            case ".pdf":
                return "application/pdf";
            case ".doc":
                return "application/msword";
            case ".docx":
                return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            case ".xls":
                return "application/vnd.ms-excel";
            case ".xlsx":
                return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            case ".ppt":
                return "application/vnd.ms-powerpoint";
            case ".pptx":
                return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
            default:
                return "application/octet-stream";
        }
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.#} {sizes[order]}";
    }

    private static string GetCssStyles()
    {
        return @"
body { font-family: Arial, sans-serif; margin: 0; padding: 0; background-color: var(--bg-color); color: var(--text-color); }
.container { padding: 20px; }
h1 { word-wrap: break-word; }
.main-content { transition: margin-right 0.3s ease, margin-bottom 0.3s ease; }
.file-list { display: flex; flex-wrap: wrap; gap: 10px; }
.file-item { background-color: var(--item-bg-color); border-radius: 5px; padding: 10px; width: calc(33.33% - 20px); box-sizing: border-box; position: relative; cursor: pointer; }
.file-checkbox { position: absolute; top: 10px; left: 10px; }
.item-icon { font-size: 2em; margin-right: 10px; }
.item-text { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.item-size { font-size: 0.9em; color: gray; margin-top: 5px; }
.breadcrumbs { margin-bottom: 15px; word-wrap: break-word; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.breadcrumbs a { margin-right: 5px; }
.theme-toggle { margin-right: 10px; cursor: pointer; }
:root { --bg-color: #ffffff; --text-color: #333333; --link-color: #1a73e8; --item-bg-color: #f0f0f0; }
[data-theme='dark'] { --bg-color: #121212; --text-color: #ffffff; --link-color: #8ab4f8; --item-bg-color: #1e1e1e; }
.actions { margin-bottom: 10px; }
.file-preview {
    position: fixed;
    top: 0;
    right: 0;
    bottom: 0;
    width: 0;
    background-color: var(--item-bg-color);
    overflow: auto;
    border-left: 2px solid #ccc;
    transition: width 0.3s ease;
}
.file-preview.open { width: 40%; }
.file-preview-content { padding: 20px; }
.resizer {
    position: absolute;
    left: -5px;
    top: 0;
    bottom: 0;
    width: 10px;
    cursor: ew-resize;
    user-select: none;
}
.download-btn { display: inline-block; margin-top: 10px; padding: 10px 15px; background-color: #1a73e8; color: #fff; text-decoration: none; border-radius: 5px; }
.download-btn:hover { background-color: #1669c1; }
.close-btn { margin-bottom: 10px; }
img.preview-image { max-width: 100%; height: auto; border-radius: 5px; }
video.preview-video { max-width: 100%; height: auto; border-radius: 5px; }
audio.preview-audio { width: 100%; margin-top: 10px; }
.file-metadata { margin-top: 10px; }
@media (max-width: 800px) {
    .file-item { width: calc(50% - 20px); }
    .file-preview {
        top: auto;
        bottom: 0;
        left: 0;
        width: 100%;
        height: 0;
        border-left: none;
        border-top: 2px solid #ccc;
        transition: height 0.3s ease;
    }
    .file-preview.open { height: 50%; }
    .main-content { margin-right: 0; transition: margin-bottom 0.3s ease; }
    .resizer {
        left: 0;
        top: -5px;
        bottom: auto;
        width: 100%;
        height: 10px;
        cursor: ns-resize;
    }
}
@media (max-width: 600px) {
    .file-item { width: 100%; }
}
";
    }

    private static string GetJavaScript()
    {
        return @"
function toggleTheme() {
    const body = document.body;
    body.dataset.theme = body.dataset.theme === 'dark' ? 'light' : 'dark';
    localStorage.setItem('theme', body.dataset.theme);
}
document.addEventListener('DOMContentLoaded', () => {
    const savedTheme = localStorage.getItem('theme') || 'light';
    document.body.dataset.theme = savedTheme;
    initResizer();
});
function showPreview(event, filePath) {
    event.stopPropagation();
    const preview = document.getElementById('filePreview');
    preview.classList.add('open');
    fetch('/?path=' + filePath + '&preview=1')
        .then(response => response.text())
        .then(html => {
            document.getElementById('previewContent').innerHTML = html;
        });
}
function closePreview() {
    const preview = document.getElementById('filePreview');
    preview.classList.remove('open');
    document.getElementById('previewContent').innerHTML = '';
}
function navigateTo(path) {
    window.location.href = '/?path=' + path;
}
function downloadSelectedFiles() {
    const checkboxes = document.querySelectorAll('.file-checkbox:checked');
    if (checkboxes.length === 0) {
        alert('Выберите файлы для загрузки.');
        return;
    }
    const files = Array.from(checkboxes).map(cb => cb.dataset.path).join(',');
    const form = document.createElement('form');
    form.method = 'POST';
    form.action = '/download';
    form.style.display = 'none';
    const input = document.createElement('input');
    input.type = 'hidden';
    input.name = 'files';
    input.value = files;
    form.appendChild(input);
    document.body.appendChild(form);
    form.submit();
    document.body.removeChild(form);
}
function initResizer() {
    const preview = document.getElementById('filePreview');
    const resizer = document.createElement('div');
    resizer.className = 'resizer';
    preview.appendChild(resizer);
    let isResizing = false;

    resizer.addEventListener('mousedown', function(e) {
        e.preventDefault();
        isResizing = true;
        document.body.style.cursor = (window.innerWidth > 800) ? 'ew-resize' : 'ns-resize';
        document.body.style.userSelect = 'none';
    });

    document.addEventListener('mousemove', function(e) {
        if (!isResizing) return;
        if (window.innerWidth > 800) {
            let newWidth = document.body.offsetWidth - e.clientX;
            if (newWidth < 200) newWidth = 200;
            if (newWidth > window.innerWidth - 200) newWidth = window.innerWidth - 200;
            preview.style.width = newWidth + 'px';
            document.querySelector('.main-content').style.marginRight = newWidth + 'px';
        } else {
            let newHeight = document.body.offsetHeight - e.clientY;
            if (newHeight < 100) newHeight = 100;
            if (newHeight > window.innerHeight - 100) newHeight = window.innerHeight - 100;
            preview.style.height = newHeight + 'px';
            document.querySelector('.main-content').style.marginBottom = newHeight + 'px';
        }
    });

    document.addEventListener('mouseup', function() {
        if (isResizing) {
            isResizing = false;
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
        }
    });
}
";
    }

    private static string GetHeader()
    {
        return @"
<div class=""container"">
    <div style=""display: flex; justify-content: space-between; align-items: center; margin-bottom: 10px;"">
        <h1>Файловый Сервер</h1>
        <div>
            <span class=""theme-toggle"" onclick=""toggleTheme()"">🌗 Тема</span>
        </div>
    </div>
</div>
";
    }
}
